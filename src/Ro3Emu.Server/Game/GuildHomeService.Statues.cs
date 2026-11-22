using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class GuildHomeService
{
    private readonly WorldData _maps;
    private readonly RoleStore _roles;
    private readonly Dictionary<long, long[]> _statueHolders = [];
    // GuildSettingConfig and GuildPositionConfig. Break ties by guild join time, then role id (inferred).
    private static readonly (string Setting, int Official)[] StatueOffices =
        [("PresentNpcPosition", 1), ("LeaderNpcPosition", 2), ("BabyNpcPosition", 10)];

    private static long[] Holders(GuildStore.State g) => [.. StatueOffices.Select(office => g.Members.Values
        .Where(m => m.Official == office.Official).OrderBy(m => m.EnterTime).ThenBy(m => m.RoleId)
        .Select(m => m.RoleId).FirstOrDefault())];

    private async Task SendStatuesAsync(GameSession s)
    {
        if (s.SceneEntry != Scene || GuildOfRoom(s.InstanceId) is not { } id || s.Guild?.GuildId != id ||
            _guilds.Get(id) is not { } g || !g.Members.ContainsKey(s.Role!.Id)) return;
        var holders = Holders(g);
        _statueHolders.TryAdd(s.InstanceId, holders);
        var view = Statues(s.InstanceId, holders);
        if (view.Bases.Count > 0) await s.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    private async Task UpdateStatuesAsync(GuildStore.State g)
    {
        if (RoomOf(g.Id) is not { } room || !_statueHolders.TryGetValue(room.Id, out var previous)) return;
        var holders = Holders(g);
        if (previous.SequenceEqual(holders)) return;
        _statueHolders[room.Id] = holders;
        var view = Statues(room.Id, holders);
        var leave = new AoIViewChangeData { LeaveAoi = { view.Bases.Select(actor => actor.Uuid) } };
        foreach (var member in room.ActiveMembers)
        {
            var peer = member.Session;
            if (peer.InstanceId != room.Id || peer.SceneLoading || peer.Guild?.GuildId != g.Id) continue;
            await peer.SendGameAsync(GameAoiSink.SyncAoiView, leave);
            await peer.SendGameAsync(GameAoiSink.SyncAoiView, view);
        }
    }

    private JsonElement? SettingValue(string key) => _data.Table("GuildSettingConfig").TryGetValue(key, out var row)
        && row.TryGetProperty("_kValue", out var value) ? value : null;

    private (long Template, MapPoint Position)? StatueSetting(string key)
    {
        if (SettingValue(key) is not { ValueKind: JsonValueKind.Array } value || value.GetArrayLength() != 4) return null;
        return (value[0].GetInt64(), new MapPoint(value[1].GetSingle(), 0, value[2].GetSingle(), value[3].GetSingle()));
    }

    private AoIViewChangeData Statues(long room, long[] holders)
    {
        var view = new AoIViewChangeData();
        if (_maps.Map(Scene) is not { Born: { } born } map || StatueSetting(StatueOffices[0].Setting) is not { } center) return view;
        bool translate = map.Nav != null && map.Nav.WalkHeight(center.Position.X, center.Position.Z, born.Y) == null;
        for (int i = 0; i < StatueOffices.Length; ++i)
        {
            if (StatueSetting(StatueOffices[i].Setting) is not { } slot || NPCConfig.Get(_data, slot.Template) is not { } npc) continue;
            var p = slot.Position;
            // The export places the trio in UnionCity_002. The active GuildMap uses _001. Keep their spacing near its entry.
            if (translate) p = p with { X = born.X + p.X - center.Position.X, Y = born.Y, Z = born.Z - 6 + p.Z - center.Position.Z };
            p = map.Standable(map.Grounded(p));
            if (!map.IsWalkable(p.X, p.Y, p.Z)) continue;
            var role = holders[i] == 0 ? null : _roles.Get(holders[i]);
            long uid = GuildCampfireService.HomeActorUid(room, 3900 + i);
            var actor = SceneService.NpcEntity(new WorldNpc(uid, Scene, WorldNpcKind.Npc, (int)slot.Template,
                role?.Name ?? npc.DisplayName(_data), (int)npc.Camp, p.X, p.Y, p.Z, p.Face, default));
            actor.Gender = role?.Sex ?? 1;
            actor.EffigyData = new EffigyData { Carrer = role?.Career ?? RoleSessionService.NoviceCareer };
            if (role != null) actor.CurUseAppearances.Add(RoleProto.Appearances(role, _roles.LoadWardrobe(role.Id)));
            view.Bases.Add(actor);
        }
        return view;
    }
}
