using System;
using System.Data;
using System.Linq;
using System.Threading;
using Dapper;
using Hangfire.Logging;
using Hangfire.Storage;
using MySql.Data.MySqlClient;

namespace Hangfire.MySql.Core.JobQueue
{
    internal class MySqlJobQueue : IPersistentJobQueue
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(MySqlJobQueue));

        private readonly MySqlStorage _storage;
        private readonly MySqlStorageOptions _options;
        public MySqlJobQueue(MySqlStorage storage, MySqlStorageOptions options)
        {
	        _storage = storage ?? throw new ArgumentNullException("storage");
            _options = options ?? throw new ArgumentNullException("options");
        }

        public IFetchedJob Dequeue(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null) throw new ArgumentNullException("queues");
            if (queues.Length == 0) throw new ArgumentException("Queue array must be non-empty.", "queues");

            FetchedJob fetchedJob = null;
            MySqlConnection connection = null;

            int timeOut = 0;
            string token = string.Empty;
            bool updated = false;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                connection = _storage.CreateAndOpenConnection();

                updated = false;
                try
                {                    
                    using (new MySqlDistributedLock(_storage, "JobQueue", TimeSpan.FromSeconds(30)))
                    {
                        token = Guid.NewGuid().ToString();

                        timeOut = _options.InvisibilityTimeout.Negate().Seconds;
                        int nUpdated = connection.Execute(
                            "update JobQueue set FetchedAt = UTC_TIMESTAMP(), FetchToken = @fetchToken " +
                            "where (FetchedAt is null or FetchedAt < DATE_ADD(UTC_TIMESTAMP(), INTERVAL @timeout SECOND)) " +
                            "   and Queue in @queues " +
                            "LIMIT 1;",
                            new
                            {
                                queues = queues,
                                timeout = timeOut,
                                fetchToken = token
                            });

                        updated = true;
                        if (nUpdated != 0)
                        {
                            fetchedJob =
                                connection
                                    .Query<FetchedJob>(
                                        "select Id, JobId, Queue " +
                                        "from JobQueue " +
                                        "where FetchToken = @fetchToken;",
                                        new
                                        {
                                            fetchToken = token
                                        })
                                    .SingleOrDefault();
                        }
                    }
                }
                catch (MySqlException ex)
                {
                    Logger.ErrorException(ex.Message, ex);
                    _storage.ReleaseConnection(connection);

                    throw new Exception($"Error dequeue in {(updated ? "select" : "update")}" +
                        $"Parameters: queues = {queues}, timeout = {timeOut}, fetchToken = {token} ", ex);
                }

                if (fetchedJob == null)
                {
                    _storage.ReleaseConnection(connection);

                    cancellationToken.WaitHandle.WaitOne(_options.QueuePollInterval);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            } while (fetchedJob == null);

            return new MySqlFetchedJob(_storage, connection, fetchedJob);
        }

        public void Enqueue(IDbConnection connection, string queue, string jobId)
        {
            Logger.TraceFormat("Enqueue JobId={0} Queue={1}", jobId, queue);
            connection.Execute("insert into JobQueue (JobId, Queue) values (@jobId, @queue)", new {jobId, queue});
        }
    }
}