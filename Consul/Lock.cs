﻿using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Consul
{
    [Serializable]
    public class LockHeldException : Exception
    {
        public LockHeldException()
        {
        }

        public LockHeldException(string message)
            : base(message)
        {
        }

        public LockHeldException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected LockHeldException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class LockNotHeldException : Exception
    {
        public LockNotHeldException()
        {
        }

        public LockNotHeldException(string message)
            : base(message)
        {
        }

        public LockNotHeldException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected LockNotHeldException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class LockInUseException : Exception
    {
        public LockInUseException()
        {
        }

        public LockInUseException(string message)
            : base(message)
        {
        }

        public LockInUseException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected LockInUseException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class LockConflictException : Exception
    {
        public LockConflictException()
        {
        }

        public LockConflictException(string message)
            : base(message)
        {
        }

        public LockConflictException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected LockConflictException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }


    [Serializable]
    public class LockMaxAttemptsReachedException : Exception
    {
        public LockMaxAttemptsReachedException() { }
        public LockMaxAttemptsReachedException(string message) : base(message) { }
        public LockMaxAttemptsReachedException(string message, Exception inner) : base(message, inner) { }
        protected LockMaxAttemptsReachedException(
          SerializationInfo info,
          StreamingContext context) : base(info, context)
        { }
    }

    /// <summary>
    /// Lock is used to implement client-side leader election. It is follows the algorithm as described here: https://consul.io/docs/guides/leader-election.html.
    /// </summary>
    public class Lock : IDistributedLock
    {
        /// <summary>
        /// DefaultLockWaitTime is how long we block for at a time to check if lock acquisition is possible. This affects the minimum time it takes to cancel a Lock acquisition.
        /// </summary>
        public static readonly TimeSpan DefaultLockWaitTime = TimeSpan.FromSeconds(15);

        /// <summary>
        /// DefaultLockRetryTime is how long we wait after a failed lock acquisition before attempting to do the lock again. This is so that once a lock-delay is in effect, we do not hot loop retrying the acquisition.
        /// </summary>
        public static readonly TimeSpan DefaultLockRetryTime = TimeSpan.FromSeconds(5);

        /// <summary>
        /// DefaultMonitorRetryTime is how long we wait after a failed monitor check
        /// of a lock (500 response code). This allows the monitor to ride out brief
        /// periods of unavailability, subject to the MonitorRetries setting in the
        /// lock options which is by default set to 0, disabling this feature.
        /// </summary>
        public static readonly TimeSpan DefaultMonitorRetryTime = TimeSpan.FromSeconds(2);

        /// <summary>
        /// LockFlagValue is a magic flag we set to indicate a key is being used for a lock. It is used to detect a potential conflict with a semaphore.
        /// </summary>
        private const ulong LockFlagValue = 0x2ddccbc058a50c18;

        private readonly AsyncLock _mutex = new AsyncLock();
        private readonly object _heldLock = new object();
        private bool _isheld;
        private int _retries;

        private CancellationTokenSource _cts;
        private Task _sessionRenewTask;

        private readonly ConsulClient _client;
        internal LockOptions Opts { get; set; }
        internal string LockSession { get; set; }

        /// <summary>
        /// If the lock is held or not.
        /// Users of the Lock object should check the IsHeld property before entering the critical section of their code, e.g. in a "while (myLock.IsHeld) {criticalsection}" block.
        /// Calls to IsHeld are syncronized across threads using a lock, so multiple threads sharing a single Consul Lock will queue up reading the IsHeld property of the lock.
        /// </summary>
        public bool IsHeld
        {
            get
            {
                lock (_heldLock)
                {
                    return _isheld;
                }
            }
            private set
            {
                lock (_heldLock)
                {
                    _isheld = value;
                }
            }
        }


        internal Lock(ConsulClient c)
        {
            _client = c;
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Lock attempts to acquire the lock and blocks while doing so. Not providing a CancellationToken means the thread can block indefinitely until the lock is acquired.
        /// There is no notification that the lock has been lost, but it may be closed at any time due to session invalidation, communication errors, operator intervention, etc.
        /// It is NOT safe to assume that the lock is held until Unlock() unless the Session is specifically created without any associated health checks.
        /// Users of the Lock object should check the IsHeld property before entering the critical section of their code, e.g. in a "while (myLock.IsHeld) {criticalsection}" block.
        /// By default Consul sessions prefer liveness over safety and an application must be able to handle the lock being lost.
        /// </summary>
        public Task<CancellationToken> Acquire()
        {
            return Acquire(CancellationToken.None);
        }

        /// <summary>
        /// Lock attempts to acquire the lock and blocks while doing so.
        /// Providing a CancellationToken can be used to abort the lock attempt.
        /// There is no notification that the lock has been lost, but IsHeld may be set to False at any time due to session invalidation, communication errors, operator intervention, etc.
        /// It is NOT safe to assume that the lock is held until Unlock() unless the Session is specifically created without any associated health checks.
        /// Users of the Lock object should check the IsHeld property before entering the critical section of their code, e.g. in a "while (myLock.IsHeld) {criticalsection}" block.
        /// By default Consul sessions prefer liveness over safety and an application must be able to handle the lock being lost.
        /// </summary>
        /// <param name="ct">The cancellation token to cancel lock acquisition</param>
        public async Task<CancellationToken> Acquire(CancellationToken ct)
        {
            using(await _mutex.LockAsync().ConfigureAwait(false))
            {
                try
                {
                    if (IsHeld)
                    {
                        // Check if we already hold the lock
                        throw new LockHeldException();
                    }

                    // Don't overwrite the CancellationTokenSource until AFTER we've tested for holding, since there might be tasks that are currently running for this lock.
                    if (_cts.IsCancellationRequested)
                    {
                        _cts.Dispose();
                        _cts = new CancellationTokenSource();
                    }

                    // Check if we need to create a session first
                    if (string.IsNullOrEmpty(Opts.Session))
                    {
                        try
                        {
                            Opts.Session = await CreateSession().ConfigureAwait(false);
                            _sessionRenewTask = _client.Session.RenewPeriodic(Opts.SessionTTL, Opts.Session,
                                WriteOptions.Default, _cts.Token);
                            LockSession = Opts.Session;
                        }
                        catch (Exception ex)
                        {
                            throw new ConsulRequestException("Failed to create session", ex);
                        }
                    }
                    else
                    {
                        LockSession = Opts.Session;
                    }

                    var qOpts = new QueryOptions()
                    {
                        WaitTime = Opts.LockWaitTime
                    };

                    var attempts = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        if (attempts > 0 && Opts.LockTryOnce)
                        {
                            throw new LockMaxAttemptsReachedException("LockTryOnce is set and the lock is already held or lock delay is in effect");
                        }

                        attempts++;

                        QueryResult<KVPair> pair;
                        try
                        {
                            pair = await _client.KV.Get(Opts.Key, qOpts).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw new ConsulRequestException("Failed to read lock key", ex);
                        }

                        if (pair.Response != null)
                        {
                            if (pair.Response.Flags != LockFlagValue)
                            {
                                throw new LockConflictException();
                            }

                            // Already locked by this session
                            if (pair.Response.Session == LockSession)
                            {
                                // Don't restart MonitorLock if this session already holds the lock
                                if (IsHeld)
                                {
                                    return _cts.Token;
                                }
                                IsHeld = true;
                                MonitorLock();
                                return _cts.Token;
                            }

                            // If it's not empty, some other session must have the lock
                            if (!string.IsNullOrEmpty(pair.Response.Session))
                            {
                                qOpts.WaitIndex = pair.LastIndex;
                                continue;
                            }
                        }

                        // If the code executes this far, no other session has the lock, so try to lock it
                        var kvPair = LockEntry(Opts.Session);
                        var locked = (await _client.KV.Acquire(kvPair).ConfigureAwait(false)).Response;

                        // KV acquisition succeeded, so the session now holds the lock
                        if (locked)
                        {
                            IsHeld = true;
                            MonitorLock();
                            return _cts.Token;
                        }

                        // Handle the case of not getting the lock
                        if (ct.IsCancellationRequested)
                        {
                            _cts.Cancel();
                            throw new TaskCanceledException();
                        }

                        // Failed to get the lock, determine why by querying for the key again
                        qOpts.WaitIndex = 0;
                        pair = await _client.KV.Get(Opts.Key, qOpts).ConfigureAwait(false);

                        // If the session is not null, this means that a wait can safely happen using a long poll
                        if (pair.Response != null && pair.Response.Session != null)
                        {
                            qOpts.WaitIndex = pair.LastIndex;
                            continue;
                        }

                        // If the session is null and the lock failed to acquire, then it means a lock-delay is in effect and a timed wait must be used to avoid a hot loop.
                        try { await Task.Delay(DefaultLockRetryTime, ct).ConfigureAwait(false); }
                        catch (TaskCanceledException) {/* Ignore TaskTaskCanceledException */}
                    }
                    throw new LockNotHeldException("Unable to acquire the lock with Consul");
                }
                finally
                {
                    if (ct.IsCancellationRequested || (!IsHeld && !string.IsNullOrEmpty(Opts.Session)))
                    {
                        _cts.Cancel();
                        if (_sessionRenewTask != null)
                        {
                            try
                            {
                                await _sessionRenewTask.ConfigureAwait(false);
                            }
                            catch (AggregateException)
                            {
                                // Ignore AggregateExceptions from the tasks during Release, since if the Renew task died, the developer will be Super Confused if they see the exception during Release.
                            }
                        }
                        else
                        {
                            await _client.Session.Destroy(Opts.Session).ConfigureAwait(false);
                        }
                        Opts.Session = null;
                    }
                }
            }
        }

        /// <summary>
        /// Unlock released the lock. It is an error to call this if the lock is not currently held.
        /// </summary>
        public async Task Release()
        {
            using (await _mutex.LockAsync().ConfigureAwait(false))
            {
                try
                {
                    if (!IsHeld)
                    {
                        throw new LockNotHeldException();
                    }
                    IsHeld = false;

                    _cts.Cancel();

                    var lockEnt = LockEntry(Opts.Session);

                    Opts.Session = null;
                    await _client.KV.Release(lockEnt).ConfigureAwait(false);
                }
                finally
                {
                    if (_sessionRenewTask != null)
                    {
                        if (_sessionRenewTask != null)
                        {
                            try
                            {
                                await _sessionRenewTask.ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Ignore Exceptions from the tasks during Release, since if the Renew task died, the developer will be Super Confused if they see the exception during Release.
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Destroy is used to cleanup the lock entry. It is not necessary to invoke. It will fail if the lock is in use.
        /// </summary>
        public async Task Destroy()
        {
            using (await _mutex.LockAsync().ConfigureAwait(false))
            {
                if (IsHeld)
                {
                    throw new LockHeldException();
                }

                var pair = (await _client.KV.Get(Opts.Key).ConfigureAwait(false)).Response;

                if (pair == null)
                {
                    return;
                }

                if (pair.Flags != LockFlagValue)
                {
                    throw new LockConflictException();
                }

                if (!string.IsNullOrEmpty(pair.Session))
                {
                    throw new LockInUseException();
                }

                var didRemove = (await _client.KV.DeleteCAS(pair).ConfigureAwait(false)).Response;

                if (!didRemove)
                {
                    throw new LockInUseException();
                }
            }
        }

        /// <summary>
        /// MonitorLock is a long running routine to monitor a lock ownership. It sets IsHeld to false if we lose our leadership.
        /// </summary>
        private Task MonitorLock()
        {
            return Task.Run(async () =>
            {
                try
                {
                    var opts = new QueryOptions() { Consistency = ConsistencyMode.Consistent };
                    _retries = Opts.MonitorRetries;
                    while (IsHeld && !_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Check to see if the current session holds the lock
                            var pair = await _client.KV.Get(Opts.Key, opts).ConfigureAwait(false);
                            if (pair.Response != null)
                            {
                                _retries = Opts.MonitorRetries;

                                // Lock is no longer held! Shut down everything.
                                if (pair.Response.Session != Opts.Session)
                                {
                                    IsHeld = false;
                                    _cts.Cancel();
                                    return;
                                }

                                // Lock is still held, start a blocking query
                                opts.WaitIndex = pair.LastIndex;
                                continue;
                            }
                            else
                            {
                                // Failsafe in case the KV store is unavailable
                                IsHeld = false;
                                _cts.Cancel();
                                return;
                            }
                        }
                        catch (Exception)
                        {
                            if (_retries > 0)
                            {
                                await Task.Delay(Opts.MonitorRetryTime, _cts.Token).ConfigureAwait(false);
                                _retries--;
                                opts.WaitIndex = 0;
                                continue;
                            }
                            throw;
                        }
                    }

                }
                finally
                {
                    IsHeld = false;
                }
            });
        }

        /// <summary>
        /// CreateSession is used to create a new managed session
        /// </summary>
        /// <returns>The session ID</returns>
        private async Task<string> CreateSession()
        {
            var se = new SessionEntry
            {
                Name = Opts.SessionName,
                TTL = Opts.SessionTTL
            };
            return (await _client.Session.Create(se).ConfigureAwait(false)).Response;
        }

        /// <summary>
        /// LockEntry returns a formatted KVPair for the lock
        /// </summary>
        /// <param name="session">The session ID</param>
        /// <returns>A KVPair with the lock flag set</returns>
        private KVPair LockEntry(string session)
        {
            return new KVPair(Opts.Key)
            {
                Value = Opts.Value,
                Session = session,
                Flags = LockFlagValue
            };
        }
    }

    /// <summary>
    /// LockOptions is used to parameterize the Lock behavior.
    /// </summary>
    public class LockOptions
    {
        /// <summary>
        ///  DefaultLockSessionName is the Session Name we assign if none is provided
        /// </summary>
        private const string DefaultLockSessionName = "Consul API Lock";

        /// <summary>
        /// DefaultLockSessionTTL is the default session TTL if no Session is provided when creating a new Lock. This is used because we do not have another other check to depend upon.
        /// </summary>
        private readonly TimeSpan DefaultLockSessionTTL = TimeSpan.FromSeconds(15);

        public string Key { get; set; }
        public byte[] Value { get; set; }
        public string Session { get; set; }
        public string SessionName { get; set; }
        public TimeSpan SessionTTL { get; set; }
        public int MonitorRetries { get; set; }
        public TimeSpan MonitorRetryTime { get; set; }
        public TimeSpan LockWaitTime { get; set; }
        public bool LockTryOnce { get; set; }

        public LockOptions(string key)
        {
            Key = key;
            SessionName = DefaultLockSessionName;
            SessionTTL = DefaultLockSessionTTL;
            MonitorRetryTime = Lock.DefaultMonitorRetryTime;
            LockWaitTime = Lock.DefaultLockWaitTime;
        }
    }

    public partial class ConsulClient : IConsulClient
    {
        /// <summary>
        /// CreateLock returns an unlocked lock which can be used to acquire and release the mutex. The key used must have write permissions.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public IDistributedLock CreateLock(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }
            return CreateLock(new LockOptions(key));
        }

        /// <summary>
        /// CreateLock returns an unlocked lock which can be used to acquire and release the mutex. The key used must have write permissions.
        /// </summary>
        /// <param name="opts"></param>
        /// <returns></returns>
        public IDistributedLock CreateLock(LockOptions opts)
        {
            if (opts == null)
            {
                throw new ArgumentNullException("opts");
            }
            return new Lock(this) { Opts = opts };
        }

        /// <summary>
        /// AcquireLock creates a lock that is already pre-acquired and implements IDisposable to be used in a "using" block
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public Task<IDistributedLock> AcquireLock(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }
            return AcquireLock(new LockOptions(key), CancellationToken.None);
        }

        /// <summary>
        /// AcquireLock creates a lock that is already pre-acquired and implements IDisposable to be used in a "using" block
        /// </summary>
        /// <param name="key"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task<IDistributedLock> AcquireLock(string key, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }
            return AcquireLock(new LockOptions(key), ct);
        }


        /// <summary>
        /// AcquireLock creates a lock that is already pre-acquired and implements IDisposable to be used in a "using" block
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task<IDistributedLock> AcquireLock(LockOptions opts)
        {
            return AcquireLock(opts, CancellationToken.None);
        }

        /// <summary>
        /// AcquireLock creates a lock that is already pre-acquired and implements IDisposable to be used in a "using" block
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<IDistributedLock> AcquireLock(LockOptions opts, CancellationToken ct)
        {
            if (opts == null)
            {
                throw new ArgumentNullException("opts");
            }

            var l = CreateLock(opts);
            await l.Acquire(ct).ConfigureAwait(false);
            return l;
        }

        /// <summary>
        /// ExecuteLock accepts a delegate to execute in the context of a lock, releasing the lock when completed.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public Task ExecuteLocked(string key, Action action)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }
            return ExecuteLocked(new LockOptions(key), CancellationToken.None, action);
        }

        /// <summary>
        /// ExecuteLock accepts a delegate to execute in the context of a lock, releasing the lock when completed.
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public Task ExecuteLocked(LockOptions opts, Action action)
        {
            if (opts == null)
            {
                throw new ArgumentNullException("opts");
            }
            return ExecuteLocked(opts, CancellationToken.None, action);
        }
        /// <summary>
        /// ExecuteLock accepts a delegate to execute in the context of a lock, releasing the lock when completed.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="ct"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public Task ExecuteLocked(string key, CancellationToken ct, Action action)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }
            return ExecuteLocked(new LockOptions(key), ct, action);
        }

        /// <summary>
        /// ExecuteLock accepts a delegate to execute in the context of a lock, releasing the lock when completed.
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="ct"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public async Task ExecuteLocked(LockOptions opts, CancellationToken ct, Action action)
        {
            if (opts == null)
            {
                throw new ArgumentNullException("opts");
            }
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }

            var l = await AcquireLock(opts, ct).ConfigureAwait(false);

            try
            {
                if (!l.IsHeld)
                {
                    throw new LockNotHeldException("Could not obtain the lock");
                }
                action();
            }
            finally
            {
                await l.Release().ConfigureAwait(false);
            }
        }    
    }
}