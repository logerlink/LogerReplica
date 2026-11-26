using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 负责 EF Core 读写分离的命令路由和读节点冷却。
    /// </summary>
    public sealed class ReadWriteSplittingInterceptor : DbCommandInterceptor
    {
        private readonly string _masterConnectionString;
        private readonly ReadNode[] _readNodes;
        private readonly TimeSpan _nodeCooldown;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _coolingNodes =
            new ConcurrentDictionary<string, DateTimeOffset>();
        private static readonly AsyncLocal<bool> HasWrittenInCurrentScope =
            new AsyncLocal<bool>();
        private int _nextWeightedSlot = -1;

        /// <summary>
        /// 初始化读写命令路由拦截器。
        /// </summary>
        /// <param name="configuration">包含主库和从库节点配置的应用配置。</param>
        public ReadWriteSplittingInterceptor(IConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            _masterConnectionString = configuration.GetConnectionString("MasterDb");
            var slaveConnectionStrings = configuration
                .GetSection("ConnectionStrings:SlaveDbs")
                .Get<string[]>() ?? new string[0];
            var cooldownSeconds = configuration.GetValue<int?>(
                "ConnectionStrings:ReadNodeCooldownSeconds") ?? 60;

            _nodeCooldown = TimeSpan.FromSeconds(cooldownSeconds);
            _readNodes = slaveConnectionStrings
                .Select((connectionString, index) => new ReadNode(
                    "Replica-" + (index + 1),
                    connectionString,
                    false))
                .Concat(new[] { new ReadNode("Master", _masterConnectionString, true) })
                .ToArray();

            ValidateConfiguration();
        }

        /// <summary>
        /// 拦截同步非查询命令并将其切换到主库。
        /// </summary>
        /// <param name="command">数据库命令。</param>
        /// <param name="eventData">命令事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <returns>原始拦截结果。</returns>
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            HasWrittenInCurrentScope.Value = true;
            OpenMasterConnection(command.Connection);
            return base.NonQueryExecuting(command, eventData, result);
        }

        /// <summary>
        /// 拦截异步非查询命令并将其切换到主库。
        /// </summary>
        /// <param name="command">数据库命令。</param>
        /// <param name="eventData">命令事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步拦截结果。</returns>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            HasWrittenInCurrentScope.Value = true;
            await OpenMasterConnectionAsync(command.Connection, cancellationToken)
                .ConfigureAwait(false);
            return await base.NonQueryExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 拦截同步查询并在需要时切换到主库或读节点。
        /// </summary>
        /// <param name="command">数据库命令。</param>
        /// <param name="eventData">命令事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <returns>原始拦截结果。</returns>
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (RequiresMaster(command) || ConnectionRoutingState.IsMaster(command.Connection))
            {
                // 如果当前命令需要访问主库，或者连接已经标记为主库，则切换到主库连接。
                OpenMasterConnection(command.Connection);
            }
            else if (command.Connection == null
                || command.Connection.State != ConnectionState.Open)
            {
                OpenReadConnection(command.Connection);
            }

            return base.ReaderExecuting(command, eventData, result);
        }

        /// <summary>
        /// 拦截异步查询并在需要时切换到主库或读节点。
        /// </summary>
        /// <param name="command">数据库命令。</param>
        /// <param name="eventData">命令事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步拦截结果。</returns>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (RequiresMaster(command) || ConnectionRoutingState.IsMaster(command.Connection))
            {
                // 如果当前命令需要访问主库，或者连接已经标记为主库，则切换到主库连接。
                await OpenMasterConnectionAsync(command.Connection, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (command.Connection == null
                || command.Connection.State != ConnectionState.Open)
            {
                await OpenReadConnectionAsync(command.Connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 判断当前命令是否必须访问主库。
        /// </summary>
        /// <param name="command">待执行的数据库命令。</param>
        /// <returns>必须访问主库时返回 <see langword="true"/>。</returns>
        private static bool RequiresMaster(DbCommand command)
        {
            return DatabaseCommandClassifier.RequiresMaster(
                command,
                HasWrittenInCurrentScope.Value);
        }

        /// <summary>
        /// 同步尝试打开读节点连接。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        private void OpenReadConnection(DbConnection connection)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            Exception lastException = null;
            foreach (var node in GetReadCandidates())
            {
                try
                {
                    SetConnectionString(connection, node.ConnectionString);
                    if (connection.State != ConnectionState.Open)
                    {
                        connection.Open();
                    }

                    _coolingNodes.TryRemove(node.Name, out _);
                    ConnectionCoolingState.MarkHealthy(node.ConnectionString, node.Name);
                    if (node.IsMaster)
                    {
                        ConnectionRoutingState.MarkMaster(connection);
                    }
                    else
                    {
                        ConnectionRoutingState.MarkRead(connection);
                    }
                    return;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    MarkNodeCooling(node);
                }
            }

            ResetRoundRobin();
            throw new InvalidOperationException("所有读节点均无法连接。", lastException);
        }

        /// <summary>
        /// 异步尝试打开读节点连接。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步操作。</returns>
        private async Task OpenReadConnectionAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            Exception lastException = null;
            foreach (var node in GetReadCandidates())
            {
                try
                {
                    await SetConnectionStringAsync(connection, node.ConnectionString)
                        .ConfigureAwait(false);
                    if (connection.State != ConnectionState.Open)
                    {
                        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    }

                    _coolingNodes.TryRemove(node.Name, out _);
                    ConnectionCoolingState.MarkHealthy(node.ConnectionString, node.Name);
                    if (node.IsMaster)
                    {
                        ConnectionRoutingState.MarkMaster(connection);
                    }
                    else
                    {
                        ConnectionRoutingState.MarkRead(connection);
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    MarkNodeCooling(node);
                }
            }

            ResetRoundRobin();
            throw new InvalidOperationException("所有读节点均无法连接。", lastException);
        }

        /// <summary>
        /// 同步打开主库连接。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        private void OpenMasterConnection(DbConnection connection)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            if (ConnectionRoutingState.IsMaster(connection)
                && connection.State == ConnectionState.Open)
            {
                return;
            }

            try
            {
                SetConnectionString(connection, _masterConnectionString);
                if (connection.State != ConnectionState.Open)
                {
                    connection.Open();
                }

                _coolingNodes.TryRemove(_readNodes[_readNodes.Length - 1].Name, out _);
                ConnectionCoolingState.MarkHealthy(_masterConnectionString, "写操作");
                ConnectionRoutingState.MarkMaster(connection);
            }
            catch
            {
                MarkNodeCooling(_readNodes[_readNodes.Length - 1]);
                throw;
            }
        }

        /// <summary>
        /// 异步打开主库连接。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步操作。</returns>
        private async Task OpenMasterConnectionAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            if (ConnectionRoutingState.IsMaster(connection)
                && connection.State == ConnectionState.Open)
            {
                return;
            }

            try
            {
                await SetConnectionStringAsync(connection, _masterConnectionString)
                    .ConfigureAwait(false);
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                }

                _coolingNodes.TryRemove(_readNodes[_readNodes.Length - 1].Name, out _);
                ConnectionCoolingState.MarkHealthy(_masterConnectionString, "写操作");
                ConnectionRoutingState.MarkMaster(connection);
            }
            catch
            {
                MarkNodeCooling(_readNodes[_readNodes.Length - 1]);
                throw;
            }
        }

        /// <summary>
        /// 为事务拦截器提供同步主库切换能力。
        /// </summary>
        /// <param name="connection">即将创建事务的数据库连接。</param>
        internal void OpenMasterConnectionForRouting(DbConnection connection)
        {
            OpenMasterConnection(connection);
        }

        /// <summary>
        /// 为事务拦截器提供异步主库切换能力。
        /// </summary>
        /// <param name="connection">即将创建事务的数据库连接。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步切换操作。</returns>
        internal Task OpenMasterConnectionForRoutingAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            return OpenMasterConnectionAsync(connection, cancellationToken);
        }

        /// <summary>
        /// 获取当前读请求的候选节点顺序。
        /// </summary>
        /// <returns>按优先级排列的读节点列表。</returns>
        private IReadOnlyList<ReadNode> GetReadCandidates()
        {
            var slaveNodes = _readNodes.Where(node => !node.IsMaster).ToArray();    // 排除主库节点
            var availableNodes = new List<ReadNode>();
            var availableSlaveNodes = slaveNodes
                .Where(node => !IsNodeCooling(node))        // 排除冷却状态的从库节点
                .ToArray();

            if (availableSlaveNodes.Length > 0)
            {
                var startIndex = GetWeightedStartIndex(availableSlaveNodes);
                for (var offset = 0; offset < availableSlaveNodes.Length; offset++)
                {
                    availableNodes.Add(availableSlaveNodes[
                        (startIndex + offset) % availableSlaveNodes.Length]);
                }
            }

            var masterNode = _readNodes[_readNodes.Length - 1];
            if (!IsNodeCooling(masterNode))
            {
                availableNodes.Add(masterNode);         // 非冷却，将主库节点添加到候选列表的末尾
            }

            if (availableNodes.Count > 0)
            {
                // 返回自认为健康可用的节点列表，按加权轮询顺序排列。
                return availableNodes;
            }

            // 忽略冷却状态，返回所有节点列表，按加权轮询顺序排列。
            var allNodes = new List<ReadNode>(slaveNodes.Length + 1);
            var forcedStartIndex = GetWeightedStartIndex(slaveNodes);
            for (var offset = 0; offset < slaveNodes.Length; offset++)
            {
                allNodes.Add(slaveNodes[(forcedStartIndex + offset) % slaveNodes.Length]);
            }

            allNodes.Add(masterNode);
            // 返回当前所有节点列表，按加权轮询顺序排列，忽略冷却状态。
            return allNodes;
        }

        /// <summary>
        /// 根据从库配置顺序计算加权轮询起点。
        /// </summary>
        /// <param name="slaveNodes">按配置顺序排列的从库。</param>
        /// <returns>本轮优先尝试的从库索引。</returns>
        private int GetWeightedStartIndex(IReadOnlyList<ReadNode> slaveNodes)
        {
            if (slaveNodes.Count == 0)
            {
                return 0;
            }
            /*
                2 个从库：权重 [2, 1]
                轮询：A、A、B

                3 个从库：权重 [3, 2, 1]
                轮询：A、A、A、B、B、C

                5 个从库：权重 [5, 4, 3, 2, 1]
                轮询：A、A、A、A、A、B、B、B、B、C、C、C、D、D、E
             */
            var totalWeight = slaveNodes.Count * (slaveNodes.Count + 1) / 2;
            // 没有意外的话，每次请求进来都会递增 _nextWeightedSlot，保证轮询
            var slot = (Interlocked.Increment(ref _nextWeightedSlot) & int.MaxValue)
                % totalWeight;

            for (var index = 0; index < slaveNodes.Count; index++)
            {
                var currentWeight = slaveNodes.Count - index;
                if (slot < currentWeight)
                {
                    return index;
                }

                slot -= currentWeight;
            }

            return slaveNodes.Count - 1;
        }

        /// <summary>
        /// 判断节点是否处于冷却状态。
        /// </summary>
        /// <param name="node">待读取节点。</param>
        /// <returns>节点处于冷却期时返回 <see langword="true"/>。</returns>
        private bool IsNodeCooling(ReadNode node)
        {
            return _coolingNodes.TryGetValue(node.Name, out var coolingUntil)
                && coolingUntil > DateTimeOffset.UtcNow
                || ConnectionCoolingState.IsCooling(node.ConnectionString);
        }

        /// <summary>
        /// 将节点标记为冷却状态。
        /// </summary>
        /// <param name="node">连接失败的节点。</param>
        private void MarkNodeCooling(ReadNode node)
        {
            _coolingNodes[node.Name] = DateTimeOffset.UtcNow.Add(_nodeCooldown);
            ConnectionCoolingState.MarkCooling(node.ConnectionString, node.Name, _nodeCooldown);
        }

        /// <summary>
        /// 将下一次加权轮询重置到初始位置。
        /// </summary>
        private void ResetRoundRobin()
        {
            // 重置轮询索引，从第一个节点开始尝试连接
            Interlocked.Exchange(ref _nextWeightedSlot, -1);
        }

        /// <summary>
        /// 设置连接目标，并在切换前关闭当前逻辑连接。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <param name="connectionString">目标连接字符串。</param>
        private static void SetConnectionString(DbConnection connection, string connectionString)
        {
            if (connection.State != ConnectionState.Closed)
            {
                connection.Close();
            }

            connection.ConnectionString = connectionString;
        }

        /// <summary>
        /// 异步关闭旧连接并设置新的数据库连接字符串。
        /// </summary>
        /// <param name="connection">待切换的数据库连接。</param>
        /// <param name="connectionString">目标连接字符串。</param>
        /// <returns>异步切换操作。</returns>
        private static async Task SetConnectionStringAsync(
            DbConnection connection,
            string connectionString)
        {
            if (connection.State != ConnectionState.Closed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }

            connection.ConnectionString = connectionString;
        }

        /// <summary>
        /// 校验主库、从库和冷却时间配置。
        /// </summary>
        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_masterConnectionString))
            {
                throw new InvalidOperationException("MasterDb 主库连接字符串不能为空。");
            }

            if (_nodeCooldown <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("读节点冷却时间必须大于零。");
            }

            if (_readNodes.Any(node => string.IsNullOrWhiteSpace(node.ConnectionString)))
            {
                throw new InvalidOperationException("从库节点连接字符串不能为空。");
            }
        }

        private sealed class ReadNode
        {
            /// <summary>
            /// 初始化节点描述。
            /// </summary>
            /// <param name="name">节点名称。</param>
            /// <param name="connectionString">节点连接字符串。</param>
            /// <param name="isMaster">是否为主库。</param>
            public ReadNode(string name, string connectionString, bool isMaster)
            {
                Name = name;
                ConnectionString = connectionString;
                IsMaster = isMaster;
            }

            public string Name { get; }

            public string ConnectionString { get; }

            public bool IsMaster { get; }
        }
    }
}
