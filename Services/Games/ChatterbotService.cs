﻿using Discord;
using Discord.WebSocket;
using NadekoBot.DataStructures.ModuleBehaviors;
using NadekoBot.Extensions;
using NadekoBot.Services.Database.Models;
using NadekoBot.Services.Permissions;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NadekoBot.Services.Games
{
    public class ChatterBotService : IEarlyBlockingExecutor
    {
        private readonly DiscordShardedClient _client;
        private readonly Logger _log;
        private readonly PermissionService _perms;
        private readonly CommandHandler _cmd;

        public ConcurrentDictionary<ulong, Lazy<ChatterBotSession>> ChatterBotGuilds { get; }

        public ChatterBotService(DiscordShardedClient client, PermissionService perms, IEnumerable<GuildConfig> gcs, CommandHandler cmd)
        {
            _client = client;
            _log = LogManager.GetCurrentClassLogger();
            _perms = perms;
            _cmd = cmd;

            ChatterBotGuilds = new ConcurrentDictionary<ulong, Lazy<ChatterBotSession>>(
                    gcs.Where(gc => gc.CleverbotEnabled)
                        .ToDictionary(gc => gc.GuildId, gc => new Lazy<ChatterBotSession>(() => new ChatterBotSession(gc.GuildId), true)));
        }

        public string PrepareMessage(IUserMessage msg, out ChatterBotSession cleverbot)
        {
            var channel = msg.Channel as ITextChannel;
            cleverbot = null;

            if (channel == null)
                return null;

            if (!ChatterBotGuilds.TryGetValue(channel.Guild.Id, out Lazy<ChatterBotSession> lazyCleverbot))
                return null;

            cleverbot = lazyCleverbot.Value;

            var nadekoId = _client.CurrentUser.Id;
            var normalMention = $"<@{nadekoId}> ";
            var nickMention = $"<@!{nadekoId}> ";
            string message;
            if (msg.Content.StartsWith(normalMention))
            {
                message = msg.Content.Substring(normalMention.Length).Trim();
            }
            else if (msg.Content.StartsWith(nickMention))
            {
                message = msg.Content.Substring(nickMention.Length).Trim();
            }
            else
            {
                return null;
            }

            return message;
        }

        public async Task<bool> TryAsk(ChatterBotSession cleverbot, ITextChannel channel, string message)
        {
            await channel.TriggerTypingAsync().ConfigureAwait(false);

            var response = await cleverbot.Think(message).ConfigureAwait(false);
            try
            {
                await channel.SendConfirmAsync(response.SanitizeMentions()).ConfigureAwait(false);
            }
            catch
            {
                await channel.SendConfirmAsync(response.SanitizeMentions()).ConfigureAwait(false); // try twice :\
            }
            return true;
        }

        public async Task<bool> TryExecuteEarly(DiscordShardedClient client, IGuild guild, IUserMessage usrMsg)
        {
            if (!(guild is SocketGuild sg))
                return false;
            try
            {
                var message = PrepareMessage(usrMsg, out ChatterBotSession cbs);
                if (message == null || cbs == null)
                    return false;
                
                var pc = _perms.GetCache(guild.Id);
                if (!pc.Permissions.CheckPermissions(usrMsg,
                    "cleverbot",
                    "Games".ToLowerInvariant(),
                    out int index))
                {
                    if (pc.Verbose)
                    {
                        //todo move this to permissions
                        var returnMsg = $"Permission number #{index + 1} **{pc.Permissions[index].GetCommand(_cmd.GetPrefix(guild), sg)}** is preventing this action.";
                        try { await usrMsg.Channel.SendErrorAsync(returnMsg).ConfigureAwait(false); } catch { }
                        _log.Info(returnMsg);
                    }
                    return true;
                }

                var cleverbotExecuted = await TryAsk(cbs, (ITextChannel)usrMsg.Channel, message).ConfigureAwait(false);
                if (cleverbotExecuted)
                {
                    _log.Info($@"CleverBot Executed
Server: {guild.Name} [{guild.Id}]
Channel: {usrMsg.Channel?.Name} [{usrMsg.Channel?.Id}]
UserId: {usrMsg.Author} [{usrMsg.Author.Id}]
Message: {usrMsg.Content}");
                    return true;
                }
            }
            catch (Exception ex) { _log.Warn(ex, "Error in cleverbot"); }
            return false;
        }
    }
}
