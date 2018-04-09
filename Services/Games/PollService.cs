﻿using NadekoBot.DataStructures.ModuleBehaviors;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using NLog;

namespace NadekoBot.Services.Games
{
    public class PollService : IEarlyBlockingExecutor
    {
        public ConcurrentDictionary<ulong, Poll> ActivePolls = new ConcurrentDictionary<ulong, Poll>();
        private readonly Logger _log;
        private readonly DiscordShardedClient _client;
        private readonly NadekoStrings _strings;

        public PollService(DiscordShardedClient client, NadekoStrings strings)
        {
            _log = LogManager.GetCurrentClassLogger();
            _client = client;
            _strings = strings;
        }

        public async Task<bool?> StartPoll(ITextChannel channel, IUserMessage msg, string arg, bool isPublic = false)
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.Contains(";"))
                return null;
            var data = arg.Split(';');
            if (data.Length < 3)
                return null;

            var poll = new Poll(_client, _strings, msg, data[0], data.Skip(1), isPublic: isPublic);
            if (ActivePolls.TryAdd(channel.Guild.Id, poll))
            {
                poll.OnEnded += (gid) =>
                {
                    ActivePolls.TryRemove(gid, out _);
                };

                await poll.StartPoll().ConfigureAwait(false);
                return true;
            }
            return false;
        }

        public async Task<bool> TryExecuteEarly(DiscordShardedClient client, IGuild guild, IUserMessage msg)
        {
            if (guild == null)
            {
                foreach (var kvp in ActivePolls)
                {
                    if (!kvp.Value.IsPublic)
                    {
                        if (await kvp.Value.TryVote(msg).ConfigureAwait(false))
                            return true;                        
                    }
                }
                return false;
            }

            if (!ActivePolls.TryGetValue(guild.Id, out var poll))
                return false;

            try
            {
                return await poll.TryVote(msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(ex);
            }

            return false;
        }
    }
}
