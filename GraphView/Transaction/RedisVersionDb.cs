﻿namespace GraphView.Transaction
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using ServiceStack.Redis;

    /// <summary>
    /// The basic fields defined here
    /// </summary>
    public partial class RedisVersionDb : VersionDb, IDisposable
    {
        /// <summary>
        /// The default redis config read-write host
        /// </summary>
        public static readonly string DEFAULT_REDIS_HOST = "127.0.0.1:6379";

        /// <summary>
        /// The defalut hashset key for the meta table
        /// meta table is a map of tableId to redisDbIndex
        /// </summary>          
        public static readonly string META_TABLE_KEY = "meta:tables:hashset";

        /// <summary>
        /// The defalut hashset key for the lua script command
        /// meta_script is a map from script_name to sha1
        /// </summary>
        public static readonly string META_SCRIPT_KEY = "meta:scripts:hashset";

        // <summary>
        /// The bytes of -1 in long type, should mind that it must be a long type with 8 bytes
        /// It's used to compare txId or as a return value in lua script
        /// </summary>
        public static readonly byte[] NEGATIVE_ONE_BYTES = BitConverter.GetBytes(-1L);

        // <summary>
        /// The bytes of -2 in long type, should mind that it must be a long type with 8 bytes
        /// It's used to be a return value of some errors in lua
        /// </summary>
        public static readonly byte[] NEGATIVE_TWO_BYTES = BitConverter.GetBytes(-2L);

        // <summary>
        /// The bytes of 0 in long type, should mind that it must be a long type with 8 bytes
        /// It's used to be a return value of successful operations
        /// </summary
        public static readonly byte[] ZERO_BYTES = BitConverter.GetBytes(0L);

        /// <summary>
        ///  The return error code of some functions
        /// </summary>
        internal static readonly long REDIS_CALL_ERROR_CODE = -2L;

        /// <summary>
        /// The default meta data partition
        /// </summary>
        public static readonly int META_DATA_PARTITION = 0;

        /// <summary>
        /// The default meta database index
        /// </summary>
        public static readonly long META_DB_INDEX = 0;
        
        /// <summary>
        /// The default transaction database index
        /// </summary>
        public static readonly long TX_DB_INDEX = 1;

        /// <summary>
        /// the singleton instance of RedisVersionDb
        /// </summary>
        private static volatile RedisVersionDb instance;

        /// <summary>
        /// the lock to guarantee the safety of table's creation and delete
        /// </summary>
        private readonly object tableLock;

        /// <summary>
        /// the lock to init the singleton instance
        /// </summary>
        private static readonly object initLock = new object();

        /// <summary>
        /// The response visitor to send requests
        /// </summary>
		private readonly RedisResponseVisitor responseVisitor;

        /// <summary>
        /// A flag to declare if the async monitor is active, which will be set as false
        /// when the version db is closed
        /// </summary>
        private bool AsyncMonitorActive = true;

        internal RedisClientManager RedisManager { get; private set; }

        /// <summary>
        /// A lua script manager to register lua scripts and get its sha1 by script name
        /// </summary>
        internal RedisLuaScriptManager RedisLuaManager { get; private set; }


        /// <summary>
        /// Redis Partition Host Config
        /// Host Sample:  "127.0.0.1:6379",
        /// </summary>
        private string[] readWriteHosts;

        /// <summary>
        /// Provide an option to set version db in pipelineMode or not
        /// </summary>
        public bool PipelineMode { get; set; } = false;

        private RedisVersionDb(int partitionCount, string[] readWriteHosts)
            : base(partitionCount)
        {
            this.PartitionCount = partitionCount;

            if (readWriteHosts == null || readWriteHosts.Length < partitionCount)
            {
                throw new ArgumentException("The size of readWriteHosts should be equal to or larger than partitionCount");
            }
            this.readWriteHosts = readWriteHosts;

            this.tableLock = new object();
            this.responseVisitor = new RedisResponseVisitor();

            this.Setup();

            for (int pid = 0; pid < this.PartitionCount; pid++)
            {
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(
                    RedisVersionDb.TX_DB_INDEX, pid);
                this.dbVisitors[pid] = new RedisVersionDbVisitor(clientPool);
            }
        }

        public static RedisVersionDb Instance(int partitionCount = 1, string[] readWriteHosts = null)
        {
            if (RedisVersionDb.instance == null)
            {
                lock (RedisVersionDb.initLock)
                {
                    if (RedisVersionDb.instance == null)
                    {
                        if (readWriteHosts == null || readWriteHosts.Length == 0)
                        {
                            RedisVersionDb.instance = new RedisVersionDb(
                                1, new string[] { RedisVersionDb.DEFAULT_REDIS_HOST });
                        }
                        else
                        {
                            RedisVersionDb.instance = new RedisVersionDb(partitionCount, readWriteHosts);
                        }
                    }
                }
            }
            return RedisVersionDb.instance;
        }
    }

    /// <summary>
    /// Methods related the version db itself
    /// </summary>
    public partial class RedisVersionDb
    {
        /// <summary>
        /// Get redisDbIndex from meta hashset by tableId
        /// Take the System.Nullable<long> to wrap the return result, as it could 
        /// return null if the table has not been found
        /// </summary>
        /// <returns></returns>
        protected long? GetTableRedisDbIndex(string tableId)
        {
            using (RedisClient redisClient = this.RedisManager.
                GetClient(RedisVersionDb.META_DB_INDEX, RedisVersionDb.META_DATA_PARTITION))
            {
                byte[] keyBytes = Encoding.ASCII.GetBytes(tableId);
                byte[] valueBytes = redisClient.HGet(RedisVersionDb.META_TABLE_KEY, keyBytes);

                if (valueBytes == null)
                {
                    return null;
                }

                return BitConverter.ToInt64(valueBytes, 0);
            }
        }

        /// <summary>
        /// Setup the version db and initialize the environment
        /// </summary>
        private void Setup()
        {
            // Default partition implementation
            this.PhysicalPartitionByKey = recordKey => recordKey.GetHashCode() % this.RedisManager.RedisInstanceCount;

            // Init lua script manager, it will access the meta database
            // The first redis instance always be the meta database
            this.RedisLuaManager = new RedisLuaScriptManager(readWriteHosts[0], RedisVersionDb.META_DB_INDEX);
            this.RedisManager = new RedisClientManager(readWriteHosts, this.RedisLuaManager);
        }

        public void Dispose()
        {
            // do nothing
        }
    }

    /// <summary>
    /// This part overrides the interfaces for DDL
    /// </summary>
    public partial class RedisVersionDb
    {
		internal override VersionTable CreateVersionTable(string tableId, long redisDbIndex)
        {
            using (RedisClient redisClient = this.RedisManager.
                GetClient(RedisVersionDb.META_DB_INDEX, RedisVersionDb.META_DATA_PARTITION))
            {
                byte[] keyBytes = Encoding.ASCII.GetBytes(tableId);
                byte[] valueBytes = BitConverter.GetBytes(redisDbIndex);

                long result = redisClient.HSetNX(RedisVersionDb.META_TABLE_KEY, keyBytes, valueBytes);
                // if the tableId exists in the redis, return null
                if (result == 0)
                {
                    return null;
                }
                return this.GetVersionTable(tableId);
            }
        }

        internal override VersionTable GetVersionTable(string tableId)
        {
            if (!this.versionTables.ContainsKey(tableId))
            {
                long? redisDbIndex = this.GetTableRedisDbIndex(tableId);
                if (redisDbIndex == null)
                {
                    return null;
                }
                RedisVersionTable versionTable = new RedisVersionTable(this, tableId, redisDbIndex.Value);

                if (!this.versionTables.ContainsKey(tableId))
                {
                    lock (this.tableLock)
                    {
                        if (!this.versionTables.ContainsKey(tableId))
                        {
                            this.versionTables[tableId] = versionTable;
                        }
                    }
                }
            }
            return this.versionTables[tableId];
        }

        internal override IEnumerable<string> GetAllTables()
        {
            using (RedisClient redisClient = this.RedisManager.
               GetClient(RedisVersionDb.META_DB_INDEX, RedisVersionDb.META_DATA_PARTITION))
            {
                byte[][] returnBytes = redisClient.HGetAll(RedisVersionDb.META_TABLE_KEY);

                if (returnBytes == null || returnBytes.Length == 0)
                {
                    return null;
                }

                IList<string> tables = new List<string>(returnBytes.Length/2);
                for (int i = 0; i < returnBytes.Length; i += 2)
                {
                    string tableName = Encoding.ASCII.GetString(returnBytes[i]);
                    tables.Add(tableName);
                }

                return tables;
            }
        }

        internal override bool DeleteTable(string tableId)
        {
            using (RedisClient redisClient = this.RedisManager.
                GetClient(RedisVersionDb.META_DB_INDEX, RedisVersionDb.META_DATA_PARTITION))
            {
                byte[] keyBytes = Encoding.ASCII.GetBytes(tableId);
                long result = redisClient.HDel(RedisVersionDb.META_TABLE_KEY, keyBytes);

                if (this.versionTables.ContainsKey(tableId))
                {
                    lock (this.tableLock)
                    {
                        if (this.versionTables.ContainsKey(tableId))
                        {
                            this.versionTables.Remove(tableId);
                        }
                    }
                }
                return result == 1;
            }
        }

        internal override void Clear()
        {
            for (int pk = 0; pk < this.PartitionCount; pk++)
            {
                using (RedisClient redisClient = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, pk))
                {
                    redisClient.FlushDb();
                }
            }

            foreach (VersionTable versionTable in this.versionTables.Values)
            {
                versionTable.Clear();
            }

            this.versionTables.Clear();
        }

        internal override void AddPartition(int partitionCount)
        {
            int prePartitionCount = this.PartitionCount;
            base.AddPartition(partitionCount);

            Array.Resize(ref this.dbVisitors, partitionCount);
            for (int pid = prePartitionCount; pid < partitionCount; pid++)
            {
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(
                    RedisVersionDb.TX_DB_INDEX, pid);
                this.dbVisitors[pid] = new RedisVersionDbVisitor(clientPool);
            }

            foreach (VersionTable versionTable in this.versionTables.Values)
            {
                versionTable.AddPartition(partitionCount);
            }

            this.PartitionCount = partitionCount;
        }

        internal override void MockLoadData(int recordCount)
        {
            foreach (VersionTable versionTable in this.versionTables.Values)
            {
                versionTable.MockLoadData(recordCount);
            }
        }
    }

    /// <summary>
    /// This part is the implementation of transaction store interfaces in redis version db
    /// TxTableEntry will be stored as a hashset in redis, and txId will be the hashId.
    /// We store in this format based on the fact we should always view and update status, 
    /// commit_time and commit_lower_bound. The hashset will be with better performance
    /// than parsing and unparsing binary data.
    /// 
    /// THING TO MIND: HSETNX in ServiceStack.Redis only supports a string as hashId
    /// To use this HSETNX, we should take txId.toString() as the hashId whereever we want
    /// to operate the TxTableEntry in redis.
    /// 
    /// IT'S IMPORTANT!!!!
    /// </summary>
    public partial class RedisVersionDb
    {
        //internal override void Visit(string tableId, int partitionKey)
        //{
        //    if (tableId == VersionDb.TX_TABLE)
        //    {
        //        RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partitionKey);
        //        this.DequeueTxEntryRequest(partitionKey);
        //        foreach (TxEntryRequest req in this.flushQueues[partitionKey])
        //        {
        //            clientPool.EnqueueTxEntryRequest(req);
        //        }
        //        clientPool.Visit();
        //        this.flushQueues[partitionKey].Clear();
        //    }
        //    else
        //    {
        //        VersionTable versionTable = this.GetVersionTable(tableId);
        //        if (versionTable != null)
        //        {
        //            versionTable.Visit(partitionKey);
        //        }
        //    }
        //}

        internal override void EnqueueTxEntryRequest(long txId, TxEntryRequest txEntryRequest, int executorPK = 0)
        {
            base.EnqueueTxEntryRequest(txId, txEntryRequest, executorPK);
        }


        /// <summary>
        /// Get a unique transaction Id and store the txTableEntry into the redis
        /// This will be implemented in two steps since HSETNX can only have a field
        /// 1. try a random txId and ensure that it is unique in redis with the command HSETNX
        ///    If it is a unique id, set it in hset to occupy it with the same atomic operation
        /// 2. set other fields of txTableEntry by HSET command
        /// </summary>
        /// <returns>a transaction Id</returns>
        internal override long InsertNewTx(long txId = -1)
        {
            long ret = 0;

            if (txId < 0)
            {
                do
                {
                    txId = StaticRandom.RandIdentity();

                    string hashId = txId.ToString();
                    byte[] keyBytes = Encoding.ASCII.GetBytes(TxTableEntry.TXID_STRING);
                    byte[] valueBytes = BitConverter.GetBytes(txId);

                    int partition = this.PhysicalPartitionByKey(hashId);
                    // If the hashId doesn't exist or field doesn't exist, return 1
                    // otherwise return 0
                    if (this.PipelineMode)
                    {
                        RedisRequest request = new RedisRequest(hashId, keyBytes, valueBytes, RedisRequestType.HSetNX);
                        RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                        ret = clientPool.ProcessLongRequest(request);
                    }
                    else
                    {
                        using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                        {
                            ret = client.HSetNX(hashId, keyBytes, valueBytes);
                        }
                    }
                } while (ret == 0);
            }
            else
            {
                // txId = StaticRandom.RandIdentity();

                string hashId = txId.ToString();
                byte[] keyBytes = Encoding.ASCII.GetBytes(TxTableEntry.TXID_STRING);
                byte[] valueBytes = BitConverter.GetBytes(txId);

                int partition = this.PhysicalPartitionByKey(hashId);
                // If the hashId doesn't exist or field doesn't exist, return 1
                // otherwise return 0
                if (this.PipelineMode)
                {
                    RedisRequest request = new RedisRequest(hashId, keyBytes, valueBytes, RedisRequestType.HSetNX);
                    RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                    ret = clientPool.ProcessLongRequest(request);
                }
                else
                {
                    using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                    {
                        ret = client.HSetNX(hashId, keyBytes, valueBytes);
                    }
                }

                if (ret == 0)
                {
                    return -1;
                }
            }

            TxTableEntry txTableEntry = new TxTableEntry(txId);
            string txIdStr = txId.ToString();
            byte[][] keysBytes =
            {
                Encoding.ASCII.GetBytes(TxTableEntry.STATUS_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_TIME_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_LOWER_BOUND_STRING)
            };
            byte[][] valuesBytes =
            {
                BitConverter.GetBytes((int) txTableEntry.Status),
                BitConverter.GetBytes(txTableEntry.CommitTime),
                BitConverter.GetBytes(txTableEntry.CommitLowerBound)
            };

            int txPartition = this.PhysicalPartitionByKey(txIdStr);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(txIdStr, keysBytes, valuesBytes, RedisRequestType.HMSet);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, txPartition);
                clientPool.ProcessVoidRequest(request);
            }
            using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, txPartition))
            {
                client.HMSet(txIdStr, keysBytes, valuesBytes);
            }
            return txId;
        }

        /// <summary>
        /// Get txTableEntry with HMGET command
        /// The return fields and values' order in HGETALL isn't guaranteed in Redis
        /// To simplyify it, we take the HMGET
        /// </summary>
        /// <param name="txId"></param>
        /// <returns></returns>
        internal override TxTableEntry GetTxTableEntry(long txId)
        {
            string hashId = txId.ToString();
            byte[][] keyBytes =
            {
                Encoding.ASCII.GetBytes(TxTableEntry.STATUS_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_TIME_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_LOWER_BOUND_STRING)
            };
            byte[][] valueBytes = null;

            int partition = this.PhysicalPartitionByKey(hashId);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(hashId, keyBytes, RedisRequestType.HMGet);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                valueBytes = clientPool.ProcessValuesRequest(request);
            }
            else
            {
                using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                {
                    valueBytes = client.HMGet(hashId, keyBytes);
                }
            }
           
            if (valueBytes == null || valueBytes.Length == 0)
            {
                return null;
            }

            return new TxTableEntry(
                txId,
                (TxStatus) BitConverter.ToInt32(valueBytes[0], 0),
                BitConverter.ToInt64(valueBytes[1], 0),
                BitConverter.ToInt64(valueBytes[2],0));
        }

        /// <summary>
        /// Implemented by HSET command
        /// </summary>
        internal override void UpdateTxStatus(long txId, TxStatus status)
        {
            string hashId = txId.ToString();
            byte[] keyBytes = Encoding.ASCII.GetBytes(TxTableEntry.STATUS_STRING);
            byte[] valueBytes = BitConverter.GetBytes((int)status);
            long ret = 0;

            int partition = this.PhysicalPartitionByKey(hashId);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(hashId, keyBytes, valueBytes, RedisRequestType.HSet);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                ret = clientPool.ProcessLongRequest(request);
            }
            else
            {
                using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                {
                    ret = client.HSet(hashId, keyBytes, valueBytes);
                }
            }
        }

        /// <summary>
        /// It's implemeted by a CAS "SET_AND_GET_COMMIT_TIME"
        /// </summary>
        /// <param name="txId"></param>
        /// <param name="proposedCommitTime"></param>
        /// <returns></returns>
        internal override long SetAndGetCommitTime(long txId, long proposedCommitTime)
        {
            string hashId = txId.ToString();
            string sha1 = this.RedisLuaManager.GetLuaScriptSha1("SET_AND_GET_COMMIT_TIME");
            byte[][] keys =
            {
                Encoding.ASCII.GetBytes(hashId),
                BitConverter.GetBytes(proposedCommitTime),
                RedisVersionDb.NEGATIVE_ONE_BYTES,
            };
            byte[][] returnBytes = null;

            int partition = this.PhysicalPartitionByKey(hashId);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(keys, sha1, 1, RedisRequestType.EvalSha);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                returnBytes = clientPool.ProcessValuesRequest(request);
            }
            else
            {
                using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                {
                    returnBytes = client.EvalSha(sha1, 1, keys);
                }
            }

            if (returnBytes == null || returnBytes.Length == 0)
            {
                return -1;
            }
            return BitConverter.ToInt64(returnBytes[1], 0);
        }

        /// <summary>
        /// It's implemeted by a CAS "UPDATE_COMMIT_LOWER_BOUND"
        /// </summary>s
        /// <returns></returns>
        internal override long UpdateCommitLowerBound(long txId, long lowerBound)
        {
            string hashId = txId.ToString();
            string sha1 = this.RedisLuaManager.GetLuaScriptSha1("UPDATE_COMMIT_LOWER_BOUND");
            byte[][] keys =
            {
                Encoding.ASCII.GetBytes(hashId),
                BitConverter.GetBytes(lowerBound),
                RedisVersionDb.NEGATIVE_ONE_BYTES,
                RedisVersionDb.NEGATIVE_TWO_BYTES,
            };
            byte[][] returnBytes = null;

            int partition = this.PhysicalPartitionByKey(hashId);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(keys, sha1, 1, RedisRequestType.EvalSha);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                returnBytes = clientPool.ProcessValuesRequest(request);
            }
            else
            {
                using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                {
                    returnBytes = client.EvalSha(sha1, 1, keys);
                }
            }

            if (returnBytes == null || returnBytes.Length == 0)
            {
                return RedisVersionDb.REDIS_CALL_ERROR_CODE;
            }

            long ret = BitConverter.ToInt64(returnBytes[1], 0);
            return ret;
        }

        internal override bool RemoveTx(long txId)
        {
            string hashId = txId.ToString();
            byte[][] keyBytes =
            {
                Encoding.ASCII.GetBytes(TxTableEntry.STATUS_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_TIME_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_LOWER_BOUND_STRING)
            };

            int partition = this.PhysicalPartitionByKey(hashId);
            long ret = 0;
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(hashId, keyBytes, RedisRequestType.HDel);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, partition);
                ret = clientPool.ProcessLongRequest(request);
            }
            else
            {
                using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, partition))
                {
                    ret = client.HDel(hashId, keyBytes);
                }
            }
            // ret is the number of fields been deleted
            return ret > 0;
        }

        internal override bool RecycleTx(long txId)
        {
            string hashId = txId.ToString();
            byte[][] keysBytes =
            {
                Encoding.ASCII.GetBytes(TxTableEntry.STATUS_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_TIME_STRING),
                Encoding.ASCII.GetBytes(TxTableEntry.COMMIT_LOWER_BOUND_STRING)
            };
            byte[][] valuesBytes =
            {
                BitConverter.GetBytes((int) TxStatus.Ongoing),
                BitConverter.GetBytes(TxTableEntry.DEFAULT_COMMIT_TIME),
                BitConverter.GetBytes(TxTableEntry.DEFAULT_LOWER_BOUND)
            };

            int txPartition = this.PhysicalPartitionByKey(hashId);
            if (this.PipelineMode)
            {
                RedisRequest request = new RedisRequest(hashId, keysBytes, valuesBytes, RedisRequestType.HMSet);
                RedisConnectionPool clientPool = this.RedisManager.GetClientPool(RedisVersionDb.TX_DB_INDEX, txPartition);
                clientPool.ProcessVoidRequest(request);
            }
            using (RedisClient client = this.RedisManager.GetClient(RedisVersionDb.TX_DB_INDEX, txPartition))
            {
                client.HMSet(hashId, keysBytes, valuesBytes);
            }

            return true;
        }
    }
}
