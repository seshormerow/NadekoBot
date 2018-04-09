﻿using Discord;
using Discord.Audio;
using NadekoBot.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NadekoBot.Services.Database.Models;

namespace NadekoBot.Services.Music
{
    public enum StreamState
    {
        Resolving,
        Queued,
        Playing,
        Completed
    }

    public class MusicPlayer
    {
        private IAudioClient AudioClient { get; set; }

        /// <summary>
        /// Player will prioritize different queuer name
        /// over the song position in the playlist
        /// </summary>
        public bool FairPlay { get; set; } = false;

        /// <summary>
        /// Song will stop playing after this amount of time. 
        /// To prevent people queueing radio or looped songs 
        /// while other people want to listen to other songs too.
        /// </summary>
        public uint MaxPlaytimeSeconds { get; set; } = 0;


        // this should be written better
        public TimeSpan TotalPlaytime => 
            _playlist.Any(s => s.TotalTime == TimeSpan.MaxValue) ? 
            TimeSpan.MaxValue : 
            new TimeSpan(_playlist.Sum(s => s.TotalTime.Ticks));

        /// <summary>
        /// Users who recently got their music wish
        /// </summary>
        private ConcurrentHashSet<string> RecentlyPlayedUsers { get; } = new ConcurrentHashSet<string>();

        private readonly List<Song> _playlist = new List<Song>();
        private readonly Logger _log;
        private readonly IGoogleApiService _google;

        public IReadOnlyCollection<Song> Playlist => _playlist;

        public Song CurrentSong { get; private set; }
        public CancellationTokenSource SongCancelSource { get; private set; }
        private CancellationToken CancelToken { get; set; }

        public bool Paused { get; set; }

        public float Volume { get; private set; }

        public event Action<MusicPlayer, Song> OnCompleted = delegate { };
        public event Action<MusicPlayer, Song> OnStarted = delegate { };
        public event Action<bool> OnPauseChanged = delegate { };

        public IVoiceChannel PlaybackVoiceChannel { get; private set; }
        public ITextChannel OutputTextChannel { get; set; }

        private bool Destroyed { get; set; }
        public bool RepeatSong { get; private set; }
        public bool RepeatPlaylist { get; private set; }
        public bool Autoplay { get; set; }
        public uint MaxQueueSize { get; set; } = 0;

        private ConcurrentQueue<Action> ActionQueue { get; } = new ConcurrentQueue<Action>();

        public string PrettyVolume => $"🔉 {(int)(Volume * 100)}%";

        public event Action<Song, int> SongRemoved = delegate { };

        public MusicPlayer(IVoiceChannel startingVoiceChannel, ITextChannel outputChannel, float? defaultVolume, IGoogleApiService google)
        {
            _log = LogManager.GetCurrentClassLogger();
            _google = google;

            OutputTextChannel = outputChannel;
            Volume = defaultVolume ?? 1.0f;

            PlaybackVoiceChannel = startingVoiceChannel ?? throw new ArgumentNullException(nameof(startingVoiceChannel));
            SongCancelSource = new CancellationTokenSource();
            CancelToken = SongCancelSource.Token;

            Task.Run(async () =>
            {
                try
                {
                    while (!Destroyed)
                    {
                        try
                        {
                            if (ActionQueue.TryDequeue(out Action action))
                            {
                                action();
                            }
                        }
                        finally
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Action queue crashed");
                    _log.Warn(ex);
                }
            }).ConfigureAwait(false);

            var t = new Thread(async () =>
            {
                while (!Destroyed)
                {
                    try
                    {
                        CurrentSong = GetNextSong();

                        if (CurrentSong == null)
                            continue;

                        while (AudioClient?.ConnectionState == ConnectionState.Disconnecting || 
                            AudioClient?.ConnectionState == ConnectionState.Connecting)
                        {
                            _log.Info("Waiting for Audio client");
                            await Task.Delay(200).ConfigureAwait(false);
                        }

                        if (AudioClient == null || AudioClient.ConnectionState == ConnectionState.Disconnected)
                            AudioClient = await PlaybackVoiceChannel.ConnectAsync().ConfigureAwait(false);

                        var index = _playlist.IndexOf(CurrentSong);
                        if (index != -1)
                            RemoveSongAt(index, true);

                        OnStarted(this, CurrentSong);
                        try
                        {
                            await CurrentSong.Play(AudioClient, CancelToken);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        finally
                        {
                            OnCompleted(this, CurrentSong);
                        }


                        if (RepeatPlaylist & !RepeatSong)
                            AddSong(CurrentSong, CurrentSong.QueuerName);

                        if (RepeatSong)
                            AddSong(CurrentSong, 0);

                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Music thread almost crashed.");
                        _log.Warn(ex);
                        await Task.Delay(3000).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!CancelToken.IsCancellationRequested)
                        {
                            SongCancelSource.Cancel();
                        }
                        SongCancelSource = new CancellationTokenSource();
                        CancelToken = SongCancelSource.Token;
                        CurrentSong = null;
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                }
            });

            t.Start();
        }

        public void Next()
        {
            ActionQueue.Enqueue(() =>
            {
                Paused = false;
                SongCancelSource.Cancel();
            });
        }

        public void Stop()
        {
            ActionQueue.Enqueue(() =>
            {
                RepeatPlaylist = false;
                RepeatSong = false;
                Autoplay = false;
                _playlist.Clear();
                if (!SongCancelSource.IsCancellationRequested)
                    SongCancelSource.Cancel();
            });
        }

        public void TogglePause() => OnPauseChanged(Paused = !Paused);

        public int SetVolume(int volume)
        {
            if (volume < 0)
                volume = 0;
            if (volume > 100)
                volume = 100;

            Volume = volume / 100.0f;
            return volume;
        }

        private Song GetNextSong()
        {
            if (!FairPlay)
            {
                return _playlist.FirstOrDefault();
            }
            var song = _playlist.FirstOrDefault(c => !RecentlyPlayedUsers.Contains(c.QueuerName))
                ?? _playlist.FirstOrDefault();

            if (song == null)
                return null;

            if (RecentlyPlayedUsers.Contains(song.QueuerName))
            {
                RecentlyPlayedUsers.Clear();
            }

            RecentlyPlayedUsers.Add(song.QueuerName);
            return song;
        }

        public void Shuffle()
        {
            ActionQueue.Enqueue(() =>
            {
                var oldPlaylist = _playlist.ToArray();
                _playlist.Clear();
                _playlist.AddRange(oldPlaylist.Shuffle());
            });
        }

        public void AddSong(Song s, string username)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            ThrowIfQueueFull();
            ActionQueue.Enqueue(() =>
            {
                s.MusicPlayer = this;
                s.QueuerName = username.TrimTo(10);
                _playlist.Add(s);
            });
        }

        public void AddSong(Song s, int index)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            ActionQueue.Enqueue(() =>
            {
                _playlist.Insert(index, s);
            });
        }

        public void RemoveSong(Song s)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            ActionQueue.Enqueue(() =>
            {
                _playlist.Remove(s);
            });
        }

        public void RemoveSongAt(int index, bool silent = false)
        {
            ActionQueue.Enqueue(() =>
            {
                if (index < 0 || index >= _playlist.Count)
                    return;
                var song = _playlist.ElementAtOrDefault(index);
                if (_playlist.Remove(song) && !silent)
                {
                    SongRemoved(song, index);
                }
                
            });
        }

        public void ClearQueue()
        {
            ActionQueue.Enqueue(() =>
            {
                _playlist.Clear();
            });
        }

        public async Task UpdateSongDurationsAsync()
        {
            var curSong = CurrentSong;
            var toUpdate = _playlist.Where(s => s.SongInfo.ProviderType == MusicType.Normal &&
                                                            s.TotalTime == TimeSpan.Zero)
                                                            .ToArray();
            if (curSong != null)
            {
                Array.Resize(ref toUpdate, toUpdate.Length + 1);
                toUpdate[toUpdate.Length - 1] = curSong;
            }
            var ids = toUpdate.Select(s => s.SongInfo.Query.Substring(s.SongInfo.Query.LastIndexOf("?v=") + 3))
                              .Distinct();

            var durations = await _google.GetVideoDurationsAsync(ids);

            toUpdate.ForEach(s =>
            {
                foreach (var kvp in durations)
                {
                    if (s.SongInfo.Query.EndsWith(kvp.Key))
                    {
                        s.TotalTime = kvp.Value;
                        return;
                    }
                }
            });
        }

        public void Destroy()
        {
            ActionQueue.Enqueue(async () =>
            {
                RepeatPlaylist = false;
                RepeatSong = false;
                Autoplay = false;
                Destroyed = true;
                _playlist.Clear();

                try { await AudioClient.StopAsync(); } catch { }
                if (!SongCancelSource.IsCancellationRequested)
                    SongCancelSource.Cancel();
            });
        }

        //public async Task MoveToVoiceChannel(IVoiceChannel voiceChannel)
        //{
        //    if (audioClient?.ConnectionState != ConnectionState.Connected)
        //        throw new InvalidOperationException("Can't move while bot is not connected to voice channel.");
        //    PlaybackVoiceChannel = voiceChannel;
        //    audioClient = await voiceChannel.ConnectAsync().ConfigureAwait(false);
        //}

        public bool ToggleRepeatSong() => RepeatSong = !RepeatSong;

        public bool ToggleRepeatPlaylist() => RepeatPlaylist = !RepeatPlaylist;

        public bool ToggleAutoplay() => Autoplay = !Autoplay;

        public void ThrowIfQueueFull()
        {
            if (MaxQueueSize == 0)
                return;
            if (_playlist.Count >= MaxQueueSize)
                throw new PlaylistFullException();
        }
    }
}
