using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class GuildService
{
    public const uint QueryResearch = 32304, UpgradeResearch = 32305;
    private const int ResearchFunction = 1340, UpgradeBuildingRight = 11;
    internal const int StrongholdMode = 1000, EscortMode = 1001, SiegeMode = 1002;
    internal Func<GameSession, int>? ResearchBattleMode { get; set; }
    internal Func<GameSession, bool>? ResearchEscort { get; set; }
    private sealed record Technology(long[] Cost, long Building, long RequiredLevel, long[][] Stats, long[] Modes);
    private readonly Lazy<Dictionary<(long Group, long Level), Technology>> _technologies = new(() =>
    {
        var rows = new Dictionary<(long, long), Technology>();
        foreach (var row in GuildWarTechConfig.All(data))
        {
            // LC_ConditionList: AND of one positive GuildBuildingLevel (9008), comparison 4 (at least).
            if (LCConditionList.Get(data, row.Limit) is not { Type: 1, Inverse: false, SubConditionList: { } children }
                || children.ValueKind != JsonValueKind.Array || children.GetArrayLength() != 1
                || new LCConditionList(children[0]) is not { Type: 3, Inverse: false, Data: [9008, var building, 4, var level] }
                || row.Cost is not [126, > 0] || row.Effects.Length == 0) continue;
            var effects = row.Effects.Select(id => SpecialEffectConfig.Get(data, id)?.SpecialEffect).ToArray();
            if (effects.Any(e => e == null || e.Length == 0 || e.Any(op => op is not [1, >= 1 and <= 6, > 0]))) continue;
            rows.Add((row.Group, row.Level), new(row.Cost, building, level, [.. effects.SelectMany(e => e!)], row.Modes));
        }
        return rows;
    });

    // LC_DAL_Guild stores the level by group. The technology help says it resets at the start of a season.
    private readonly Lazy<long[]> _researchGroups = new(() => [.. GuildWarTechConfig.All(data).Select(r => r.Group).Distinct()]);
    private GuildTechInfo ResearchInfo(GuildStore.State g)
    {
        // LC_DAL_Guild shallow-copies into an existing client table. Explicit zeros also clear the previous guild/season.
        var info = new GuildTechInfo();
        bool current = g.ResearchSeason == ResearchSeason;
        foreach (long group in _researchGroups.Value)
            info.TechLevel.Add(group, current ? g.ResearchLevels.GetValueOrDefault(group) : 0);
        return info;
    }

    internal sealed record ResearchStats(long Guild, long Season, Dictionary<long, long[]> ByMode, GuildService Owner)
    {
        internal void Add(GameSession s, double[] attributes)
        {
            long mode = s.InstanceId != 0 ? Owner.ResearchBattleMode?.Invoke(s) ?? 0
                : Owner.ResearchEscort?.Invoke(s) == true ? EscortMode : 0;
            if (!ByMode.TryGetValue(mode, out var values) || Season != Owner.ResearchSeason) return;
            for (int i = 0; i < values.Length; ++i) attributes[i] += values[i];
        }
    }

    private long ResearchSeason => seasons.StartTime;

    private ResearchStats? ResearchBonuses(GuildStore.State g)
    {
        if (g.ResearchLevels.Count == 0 || g.ResearchSeason != ResearchSeason) return null;
        var byMode = new Dictionary<long, long[]>();
        foreach (var entry in g.ResearchLevels)
        {
            if (!_technologies.Value.TryGetValue((entry.Key, entry.Value), out var row)) continue;
            foreach (long mode in row.Modes)
            {
                if (!byMode.TryGetValue(mode, out var stats)) byMode.Add(mode, stats = new long[6]);
                foreach (var op in row.Stats) stats[op[1] - 1] += op[2];
            }
        }
        return new(g.Id, g.ResearchSeason, byMode, this);
    }

    private int ResearchAccess(GameSession s, GuildStore.State? g) => g == null ? ErrorCodes.NotInGuild
        : g.Status != (int)GuildStatus.Normal || !functions.IsOpen(s, ResearchFunction) ? ErrorCodes.FeatureUnavailable : 0;

    internal Task QueryResearchAsync(GameSession s) => RunGuildAsync(s, [], [], () =>
    {
        var g = Own(s);
        int result = ResearchAccess(s, g);
        return s.SendGameAsync(QueryResearch, new QueryGuildTechRes { Result = result, TechInfo = result == 0 ? ResearchInfo(g!) : null });
    });

    internal Task UpgradeResearchAsync(GameSession s, OperateGuildTechReq req) => RunGuildAsync(s, [], [], async () =>
    {
        var g = Own(s);
        int result = ResearchAccess(s, g);
        if (result == 0) result = await PurchaseResearchAsync(s, g!, req);
        await s.SendGameAsync(UpgradeResearch, new OperateGuildTechRes { Result = result, TechInfo = result == 0 ? ResearchInfo(g!) : null });
        if (result != 0 || g == null) return;
        var stats = ResearchBonuses(g);
        var updated = new QueryGuildTechRes { TechInfo = ResearchInfo(g) };
        foreach (var peer in sessions.All().Where(p => p.Role != null && p.Guild?.GuildId == g.Id))
        {
            peer.GuildResearch = stats;
            if (peer != s) await peer.SendGameAsync(QueryResearch, updated);
            if (ResearchBattleMode?.Invoke(peer) > 0 || ResearchEscort?.Invoke(peer) == true)
                await character.RefreshStatsAsync(peer);
        }
        await PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = Resource(g) });
    });

    private async Task<int> PurchaseResearchAsync(GameSession s, GuildStore.State g, OperateGuildTechReq req)
    {
        // No separate Research right is exported. Reuse the building-upgrade permission (inferred).
        if (!HasRight(g, s.Role!.Id, UpgradeBuildingRight)) return ErrorCodes.GuildPermission;
        if (!_technologies.Value.TryGetValue((req.TechId, req.Level), out var row)) return ErrorCodes.FeatureUnavailable;
        long season = ResearchSeason;
        if (g.ResearchSeason != season) { g.ResearchSeason = season; g.ResearchLevels.Clear(); }
        if (req.Level != g.ResearchLevels.GetValueOrDefault(req.TechId) + 1) return ErrorCodes.Parameter;
        if (g.Buildings.GetValueOrDefault(row.Building) < row.RequiredLevel) return ErrorCodes.GuildHallLow;
        long price = row.Cost[1];
        if (g.Materials < price) return ErrorCodes.GuildSupplies;
        g.Materials -= price;
        g.ResearchLevels[req.TechId] = req.Level;
        string source = $"{g.Id}:{season}:{req.TechId}:{req.Level}";
        var spend = new EconomyEnvelope(Guid.NewGuid().ToString("N"), s.Account!, s.Role.Id, "guild-research", source,
            "guild-research:" + source, NowMs(), [new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMaterials),
                new(EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture)), EconomyCompartment.GuildTreasury,
                -price, EconomyLegKind.Sink, EconomyReason.GuildResourceSpend)], []);
        return await SaveGuildAsync(g, economy: spend) ? 0 : ErrorCodes.GuildMissing;
    }
}
