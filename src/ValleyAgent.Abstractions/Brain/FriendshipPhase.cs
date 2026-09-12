namespace ValleyAgent.Brain
{
    public enum FriendshipPhase
    {
        Stranger,
        Acquaintance,
        Friend,
        Close,
        Partner
    }

    public static class FriendshipPhaseHelper
    {
        public static FriendshipPhase FromFriendship(int points)
        {
            if (points > 2000)
            {
                return FriendshipPhase.Partner;
            }

            return points > 1000 ? FriendshipPhase.Close
                : points > 500 ? FriendshipPhase.Friend
                : points > 250 ? FriendshipPhase.Acquaintance : FriendshipPhase.Stranger;
        }

        public static string ToChineseLabel(FriendshipPhase phase)
        {
            return phase switch
            {
                FriendshipPhase.Stranger => "陌生人",
                FriendshipPhase.Acquaintance => "认识的人",
                FriendshipPhase.Friend => "朋友",
                FriendshipPhase.Close => "亲密好友",
                FriendshipPhase.Partner => "伴侣/至交",
                _ => "陌生人"
            };
        }

        public static string ToBehaviorGuidance(FriendshipPhase phase, string npcName)
        {
            return phase switch
            {
                FriendshipPhase.Stranger => $"这是两人首次或极少交谈。对话要简短、保持距离。{npcName} 还不确定农场主是否值得结交。",
                FriendshipPhase.Acquaintance => $"两人见面能认出来，但刚开始认识。不要分享私事，保持礼貌的距离。",
                FriendshipPhase.Friend => $"两人正在成为朋友。开始了解对方生活，会闲聊但不会特别热情。",
                FriendshipPhase.Close => $"两人是亲密好友，互相非常了解。会分享个人想法，愿意一起共度时间。",
                FriendshipPhase.Partner => $"两人是伴侣/至交。{npcName} 和农场主关系深厚。对话体现完全的信任和亲密。",
                _ => $"对话保持礼貌距离。"
            };
        }
    }
}
