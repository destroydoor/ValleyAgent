using Microsoft.Xna.Framework;

namespace ValleyAgent.Config
{
    public static class GameConstants
    {
        public const string HarmonyId = "dandm1.ValleyAgent";
        public const string SaveDataKey = "ValleyAgent_Agents";
        public const string StructuredSaveDataKey = "ValleyAgent_Structured";
        public const string DefaultFarmerNickname = "新来的农夫";
        public const string TestModId = "dandm1.ValleyAgent.TestMod";

        public static readonly Color HealthBarBgColor = new(60, 60, 60, 180);
        public static readonly Color HealthBarHighColor = new(60, 220, 60, 220);
        public static readonly Color HealthBarMidColor = new(220, 180, 60, 220);
        public static readonly Color HealthBarLowColor = new(220, 60, 60, 220);
        public const float HealthBarHighThreshold = 0.5f;
        public const float HealthBarMidThreshold = 0.25f;
        public const int HealthBarWidth = 32;
        public const int HealthBarHeight = 4;
        public const float HealthBarOffsetX = 16f;
        public const float HealthBarOffsetY = -16f;
    }
}
