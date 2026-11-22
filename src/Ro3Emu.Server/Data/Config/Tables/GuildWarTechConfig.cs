using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

[ConfigTable("GuildWarTechConfig")]
public readonly partial record struct GuildWarTechConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iLv")] public partial long Level { get; }
    [ConfigField("_iLimit")] public partial long Limit { get; }
    [ConfigField("_iCostMoney")] public partial long[] Cost { get; }
    [ConfigField("_iSpSkillID")] public partial long[] Effects { get; }
    [ConfigField("_iPlayingMethodID")] public partial long[] Modes { get; }
}
