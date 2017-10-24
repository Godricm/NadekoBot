﻿using NadekoBot.Core.Services.Impl;
using NLog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NadekoBot.Common.ShardCom;
using StackExchange.Redis;
using Newtonsoft.Json;
using NadekoBot.Extensions;
using NadekoBot.Common.Collections;
using System.Linq;
using System.Collections.Generic;

namespace NadekoBot.Core.Services
{
    public class ShardsCoordinator
    {
        private class ShardsCoordinatorQueue
        {
            private readonly object _locker = new object();
            private readonly HashSet<int> _set = new HashSet<int>();
            private readonly Queue<int> _queue = new Queue<int>();
            public int Count => _queue.Count;

            public void Enqueue(int i)
            {
                lock (_locker)
                {
                    if (_set.Add(i))
                        _queue.Enqueue(i);
                }
            }

            public bool TryDequeue(out int id)
            {
                lock (_locker)
                {
                    if (_queue.TryDequeue(out id))
                    {
                        _set.Remove(id);
                        return true;
                    }
                }
                return false;
            }
        }

        private readonly BotCredentials _creds;
        private readonly string _key;
        private readonly Process[] _shardProcesses;

        private readonly Logger _log;
        private readonly int _curProcessId;
        private readonly ConnectionMultiplexer _redis;
        private ShardComMessage _defaultShardState;

        private ShardsCoordinatorQueue _shardStartQueue =
            new ShardsCoordinatorQueue();

        private ConcurrentHashSet<int> _shardRestartWaitingList =
            new ConcurrentHashSet<int>();

        public ShardsCoordinator()
        {
            //load main stuff
            LogSetup.SetupLogger();
            _log = LogManager.GetCurrentClassLogger();
            _creds = new BotCredentials();

            _log.Info("Starting NadekoBot v" + StatsService.BotVersion);

            _key = _creds.RedisKey();
            _redis = ConnectionMultiplexer.Connect("127.0.0.1");

            //setup initial shard statuses
            _defaultShardState = new ShardComMessage()
            {
                ConnectionState = Discord.ConnectionState.Disconnected,
                Guilds = 0,
                Time = DateTime.UtcNow
            };
            var db = _redis.GetDatabase();
            //clear previous statuses
            db.KeyDelete(_key + "_shardstats"); 

            _shardProcesses = new Process[_creds.TotalShards];
            for (int i = 0; i < _creds.TotalShards; i++)
            {
                //add it to the list of shards which should be started
#if DEBUG
                if (i > 0)
                    _shardStartQueue.Enqueue(i);
#else
                _shardStartQueue.Enqueue(i); 
#endif
                //set the shard's initial state in redis cache
                _defaultShardState.ShardId = i;
                //this is to avoid the shard coordinator thinking that
                //the shard is unresponsive while startup up
                _defaultShardState.Time = DateTime.UtcNow + TimeSpan.FromSeconds(20 * i);
                db.ListRightPush(_key + "_shardstats",
                    JsonConvert.SerializeObject(_defaultShardState),
                    flags: CommandFlags.FireAndForget);
            }

            _curProcessId = Process.GetCurrentProcess().Id;

            //subscribe to shardcoord events
            var sub = _redis.GetSubscriber();

            //send is called when shard status is updated. Every 7.5 seconds atm
            sub.Subscribe(_key + "_shardcoord_send", 
                OnDataReceived,
                CommandFlags.FireAndForget);

            //restart is called when shzard should be stopped and then started again
            sub.Subscribe(_key + "_shardcoord_restart",
                OnRestart,
                CommandFlags.FireAndForget);

            //called to kill the shard
            sub.Subscribe(_key + "_shardcoord_stop",
                OnStop,
                CommandFlags.FireAndForget);

            //called kill the bot
            sub.Subscribe(_key + "_die",
                (ch, x) => Environment.Exit(0),
                CommandFlags.FireAndForget);
        }

        private void OnStop(RedisChannel ch, RedisValue data)
        {
            var shardId = JsonConvert.DeserializeObject<int>(data);
            OnStop(shardId);
        }

        private void OnStop(int shardId)
        {
            var db = _redis.GetDatabase();
            _defaultShardState.ShardId = shardId;
            db.ListSetByIndex(_key + "_shardstats",
                    shardId,
                    JsonConvert.SerializeObject(_defaultShardState),
                    CommandFlags.FireAndForget);
            var p = _shardProcesses[shardId];
            _shardProcesses[shardId] = null;
            try { p?.Kill(); } catch { }
            try { p?.Dispose(); } catch { }
        }

        private void OnRestart(RedisChannel ch, RedisValue data)
        {
            var shardId = JsonConvert.DeserializeObject<int>(data);
            OnStop(shardId);
            _shardProcesses[shardId] = StartShard(shardId);
        }

        private void OnDataReceived(RedisChannel ch, RedisValue data)
        {
            var msg = JsonConvert.DeserializeObject<ShardComMessage>(data);
            if (msg == null)
                return;
            var db = _redis.GetDatabase();
            //sets the shard state
            db.ListSetByIndex(_key + "_shardstats",
                    msg.ShardId,
                    data,
                    CommandFlags.FireAndForget);
            if (msg.ConnectionState == Discord.ConnectionState.Disconnected
                || msg.ConnectionState == Discord.ConnectionState.Disconnecting)
            {
                _log.Error("!!! SHARD {0} IS IN {1} STATE !!!", msg.ShardId, msg.ConnectionState.ToString());

                OnShardUnavailable(msg.ShardId);
            }
            else
            {
                // remove the shard from the waiting list if it's on it,
                // because it's connected/connecting now
                _shardRestartWaitingList.TryRemove(msg.ShardId);
            }
            return;
        }

        private void OnShardUnavailable(int shardId)
        {
            //if the shard is dc'd, add it to the restart waiting list
            if (!_shardRestartWaitingList.Add(shardId))
            {
                //if it's already on the waiting list
                //stop the shard
                OnStop(shardId);
                //add it to the start queue (start the shard)
                _shardStartQueue.Enqueue(shardId);
                //remove it from the waiting list
                _shardRestartWaitingList.TryRemove(shardId);
            }
        }

        public async Task RunAsync()
        {
            //this task will complete when the initial start of the shards 
            //is complete, but will keep running in order to restart shards
            //which are disconnected for too long
            TaskCompletionSource<bool> tsc = new TaskCompletionSource<bool>();
            var _ = Task.Run(async () =>
            {
                do
                {
                    //start a shard which is scheduled for start every 6 seconds 
                    while (_shardStartQueue.TryDequeue(out var id))
                    {
                        // if the shard is on the waiting list again
                        // remove it since it's starting up now

                        _shardRestartWaitingList.TryRemove(id);
                        //if the task is already completed,
                        //it means the initial shard starting is done,
                        //and this is an auto-restart
                        if (tsc.Task.IsCompleted)
                        {
                            _log.Warn("Auto-restarting shard {0}", id);
                        }
                        var p = StartShard(id);

                        _shardProcesses[id] = p;
                        await Task.Delay(6000).ConfigureAwait(false);
                    }
                    tsc.TrySetResult(true);
                    await Task.Delay(6000).ConfigureAwait(false);
                }
                while (true);
                // ^ keep checking for shards which need to be restarted
            });

            //restart unresponsive shards
            _ = Task.Run(async () =>
            {
                //after all shards have started initially
                await tsc.Task.ConfigureAwait(false);
                while (true)
                {
                    try
                    {
                        var db = _redis.GetDatabase();
                        //get all shards which didn't communicate their status in the last 30 seconds
                        var all = db.ListRange(_creds.RedisKey() + "_shardstats")
                           .Select(x => JsonConvert.DeserializeObject<ShardComMessage>(x));
                        var statuses = all
                           .Where(x => x.Time < DateTime.UtcNow - TimeSpan.FromSeconds(30));

                        if (!statuses.Any())
                        {
#if !DEBUG
                            for (var i = 0; i < _shardProcesses.Length; i++)
                            {
                                var p = _shardProcesses[i];
                                if (p == null || p.HasExited)
                                    _shardStartQueue.Enqueue(i);
                            }
#endif
                        }
                        else
                        {
                            foreach (var s in statuses)
                            {
                                OnStop(s.ShardId);
                                _shardStartQueue.Enqueue(s.ShardId);

                                //to prevent shards which are already scheduled for restart to be scheduled again
                                s.Time = DateTime.UtcNow + TimeSpan.FromSeconds(30 * _shardStartQueue.Count);
                                db.ListSetByIndex(_key + "_shardstats", s.ShardId,
                                    JsonConvert.SerializeObject(s), CommandFlags.FireAndForget);
                                _log.Warn("Shard {0} is scheduled for a restart because it's unresponsive.", s.ShardId);
                            }
                        }
                    }
                    catch (Exception ex) { _log.Error(ex); throw; }
                    finally
                    {
                        await Task.Delay(10000).ConfigureAwait(false);
                    }
                }
            });

            await tsc.Task.ConfigureAwait(false);
            return;
        }

        private Process StartShard(int shardId)
        {
            return Process.Start(new ProcessStartInfo()
            {
                FileName = _creds.ShardRunCommand,
                Arguments = string.Format(_creds.ShardRunArguments, shardId, _curProcessId, "")
            });
            // last "" in format is for backwards compatibility
            // because current startup commands have {2} in them probably
        }

        public async Task RunAndBlockAsync()
        {
            try
            {
                await RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex);
                foreach (var p in _shardProcesses)
                {
                    try { p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
                return;
            }

            await Task.Delay(-1);
        }
    }
}