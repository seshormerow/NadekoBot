﻿using Discord;
using NadekoBot.Services.Database.Models;
using System.Collections.Concurrent;

namespace NadekoBot.Services.Administration
{
    public enum ProtectionType
    {
        Raiding,
        Spamming,
    }

    public class AntiRaidStats
    {
        public AntiRaidSetting AntiRaidSettings { get; set; }
        public int UsersCount { get; set; }
        public ConcurrentHashSet<IGuildUser> RaidUsers { get; set; } = new ConcurrentHashSet<IGuildUser>();
    }

    public class AntiSpamStats
    {
        public AntiSpamSetting AntiSpamSettings { get; set; }
        public ConcurrentDictionary<ulong, UserSpamStats> UserStats { get; set; }
            = new ConcurrentDictionary<ulong, UserSpamStats>();
    }
}
