using System.Collections.Generic;

namespace ValleyAgent.RAG.Models;

/// <summary>
///     Represents Stardew Valley game mechanics rules and constants.
/// </summary>
public class GameMechanics
{
    /// <summary>Friendship mechanics rules.</summary>
    public FriendshipMechanics Friendship { get; set; } = new();

    /// <summary>Gift-giving mechanics rules.</summary>
    public GiftMechanics Gifts { get; set; } = new();

    /// <summary>Marriage and dating mechanics rules.</summary>
    public MarriageMechanics Marriage { get; set; } = new();

    /// <summary>Season and time mechanics.</summary>
    public SeasonMechanics Seasons { get; set; } = new();

    /// <summary>Farming mechanics.</summary>
    public FarmingMechanics Farming { get; set; } = new();

    /// <summary>Mining and combat mechanics.</summary>
    public MiningMechanics Mining { get; set; } = new();

    /// <summary>Fishing mechanics.</summary>
    public FishingMechanics Fishing { get; set; } = new();
}

public class FriendshipMechanics
{
    /// <summary>Points per heart level.</summary>
    public int PointsPerHeart { get; set; }

    /// <summary>Maximum hearts for normal NPCs.</summary>
    public int MaxHeartsNormal { get; set; }

    /// <summary>Maximum hearts for romanceable NPCs.</summary>
    public int MaxHeartsRomanceable { get; set; }

    /// <summary>Points gained per conversation per day.</summary>
    public int ConversationPointsPerDay { get; set; }

    /// <summary>Points gained when talking to an NPC you haven't talked to today.</summary>
    public int FirstConversationBonus { get; set; }

    /// <summary>Friendship decay per day when not talked to.</summary>
    public int DecayPerDay { get; set; }

    /// <summary>Heart levels at which events trigger.</summary>
    public Dictionary<string, int> HeartEventLevels { get; set; } = new();
}

public class GiftMechanics
{
    /// <summary>Gift taste multipliers: taste name -> point value.</summary>
    public Dictionary<string, int> TasteMultipliers { get; set; } = new();

    /// <summary>Birthday gift multiplier.</summary>
    public int BirthdayMultiplier { get; set; }

    /// <summary>Maximum gifts per week per NPC.</summary>
    public int MaxGiftsPerWeek { get; set; }

    /// <summary>Maximum gifts per day per NPC.</summary>
    public int MaxGiftsPerDay { get; set; }

    /// <summary>Universal love items (loved by almost all NPCs).</summary>
    public List<string> UniversalLoves { get; set; } = new();

    /// <summary>Universal like items.</summary>
    public List<string> UniversalLikes { get; set; } = new();

    /// <summary>Universal dislike items.</summary>
    public List<string> UniversalDislikes { get; set; } = new();

    /// <summary>Universal hate items.</summary>
    public List<string> UniversalHates { get; set; } = new();
}

public class MarriageMechanics
{
    /// <summary>Hearts required to start dating.</summary>
    public int HeartsToStartDate { get; set; }

    /// <summary>Hearts required to propose marriage.</summary>
    public int HeartsToMarry { get; set; }

    /// <summary>Item required to propose (Bouquet for dating, Pendant for marriage).</summary>
    public Dictionary<string, string> RequiredItems { get; set; } = new();

    /// <summary>Roommate options (non-marriage partnerships).</summary>
    public List<string> RoommateOptions { get; set; } = new();

    /// <summary>Whether the farmhouse upgrades after marriage.</summary>
    public bool SpouseGetsRoom { get; set; }

    /// <summary>Maximum hearts after marriage (includes stardrop).</summary>
    public int MaxHeartsAfterMarriage { get; set; }
}

public class SeasonMechanics
{
    /// <summary>Days per season.</summary>
    public int DaysPerSeason { get; set; }

    /// <summary>Season names in order.</summary>
    public List<string> SeasonOrder { get; set; } = new();

    /// <summary>Time when day starts.</summary>
    public string DayStartTime { get; set; } = string.Empty;

    /// <summary>Time when player passes out (2:00 AM).</summary>
    public string PassOutTime { get; set; } = string.Empty;

    /// <summary>Energy penalty for passing out.</summary>
    public int PassOutEnergyPenalty { get; set; }

    /// <summary>Gold penalty for passing out.</summary>
    public int PassOutGoldPenalty { get; set; }
}

public class FarmingMechanics
{
    /// <summary>Number of days in each growth stage for crops.</summary>
    public Dictionary<string, int> GrowthStages { get; set; } = new();

    /// <summary>Quality levels and their multipliers.</summary>
    public Dictionary<string, double> QualityMultipliers { get; set; } = new();

    /// <summary>Whether crops die at season change.</summary>
    public bool CropsDieAtSeasonChange { get; set; }

    /// <summary>Greenhouse allows year-round growing.</summary>
    public bool GreenhouseYearRound { get; set; }
}

public class MiningMechanics
{
    /// <summary>Total mine levels.</summary>
    public int TotalMineLevels { get; set; }

    /// <summary>Skull Cavern has no bottom.</summary>
    public bool SkullCavernInfinite { get; set; }

    /// <summary>Ore types by mine depth.</summary>
    public Dictionary<string, string> OreByDepth { get; set; } = new();

    /// <summary>Monster difficulty scaling.</summary>
    public string DifficultyScaling { get; set; } = string.Empty;
}

public class FishingMechanics
{
    /// <summary>Fish difficulty levels.</summary>
    public List<string> DifficultyLevels { get; set; } = new();

    /// <summary>Weather affects fishing.</summary>
    public bool WeatherAffectsFishing { get; set; }

    /// <summary>Time of day affects fish availability.</summary>
    public bool TimeAffectsFishing { get; set; }

    /// <summary>Season affects fish availability.</summary>
    public bool SeasonAffectsFishing { get; set; }
}