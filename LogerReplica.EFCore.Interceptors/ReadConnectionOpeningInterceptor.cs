using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 在 EF Core 打开物理连接前选择读节点，并处理读节点连接失败切换。
    /// </summary>
    public sealed class ReadConnectionOpeningInterceptor : DbConnectionInterceptor
    {
        private static int _nextWeightedSlot = -1;

        private readonly ReadNode[] _nodes;
        private readonly TimeSpan _cooldown;

        /// <summary>
        /// 初始化连接打开拦截器。
        /// </summary>
        /// <param name="configuration">包含主库和从库节点配置的应用配置。</param>
        public ReadConnectionOpeningInterceptor(IConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }
            /*
             配置模板
            {
                "ConnectionStrings": {
                    "MasterDb": "Server=127.0.0.1;Port=3316;Database=my_school;Uid=root;Pwd=123456;",
                    "SlaveDbs": [
                      "Server=127.0.0.1;Port=3317;Database=my_school;Uid=root;Pwd=123456;",
                      "Server=127.0.0.1;Port=3318;Database=my_school;Uid=root;Pwd=123456;"
                    ],
                    "ReadNodeCooldownSeconds": "300" // 5分钟。连接不可用后，进行冷却，5分钟内不再尝试连接该节点。默认1分钟
                  }
            }
             */

            var masterConnectionString = configuration.GetConnectionString("MasterDb");
            var slaveConnectionStrings = configuration
                .GetSection("ConnectionStrings:SlaveDbs")
                .Get<string[]>() ?? new string[0];
            var cooldownSeconds = configuration.GetValue<int?>(
                "ConnectionStrings:ReadNodeCooldownSeconds") ?? 60;     // 默认冷却时间为60秒

            _cooldown = TimeSpan.FromSeconds(cooldownSeconds);
            _nodes = slaveConnectionStrings
                .Select((connectionString, index) => new ReadNode(
                    "Replica-" + (index + 1),
                    connectionString,
                    false))
                .Concat(new[] { new ReadNode("Master", masterConnectionString, true) })
                .ToArray();

            ValidateConfiguration(masterConnectionString);
        }

        /// <summary>
        /// 在同步打开连接前依次尝试读节点和主库。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        /// <param name="eventData">连接事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <returns>抑制 EF Core 默认打开动作的结果。</returns>
        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            OpenConnection(connection);
            return InterceptionResult.Suppress();
        }

        /// <summary>
        /// 在异步打开连接前依次尝试读节点和主库。
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        /// <param name="eventData">连接事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>抑制 EF Core 默认打开动作的结果。</returns>
        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return InterceptionResult.Suppress();
        }

        /// <summary>
        /// 记录 EF Core 默认打开连接阶段产生的节点失败。
        /// </summary>
        /// <param name="connection">连接对象。</param>
        /// <param name="eventData">连接错误事件数据。</param>
        public override void ConnectionFailed(
            DbConnection connection,
            ConnectionErrorEventData eventData)
        {
            if (eventData == null
                || !DatabaseCommandClassifier.IsSqlOrBusinessFailure(eventData.Exception))
            {
                ConnectionCoolingState.MarkCooling(
                    connection == null ? null : connection.ConnectionString,
                    "失败节点（Properly MasterDB）",
                    _cooldown);
            }

            base.ConnectionFailed(connection, eventData);
        }

        /// <summary>
        /// 异步记录 EF Core 默认打开连接阶段产生的节点失败。
        /// </summary>
        /// <param name="connection">连接对象。</param>
        /// <param name="eventData">连接错误事件数据。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步操作。</returns>
        public override Task ConnectionFailedAsync(
            DbConnection connection,
            ConnectionErrorEventData eventData,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (eventData == null
                || !DatabaseCommandClassifier.IsSqlOrBusinessFailure(eventData.Exception))
            {
                ConnectionCoolingState.MarkCooling(
                    connection == null ? null : connection.ConnectionString,
                    "失败节点（Properly MasterDB）",
                    _cooldown);
            }

            return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
        }

      

        /// <summary>
        /// 同步设置当前请求的候选节点连接字符串。并非真正连接，不会Open
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        private void OpenConnection(DbConnection connection)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            if (ConnectionRoutingState.IsMaster(connection))
            {
                // 如果连接已经标记为主库，则直接使用主库连接字符串，不再尝试从库。
                SetConnectionString(connection, _nodes[_nodes.Length - 1].ConnectionString);
                return;
            }

            var node = GetCandidates().FirstOrDefault();
            if (node == null)
            {
                throw new InvalidOperationException("没有可用的数据库节点。");
            }

            SetConnectionString(connection, node.ConnectionString);
            MarkRoutingState(connection, node);
        }

        /// <summary>
        /// 异步设置当前请求的候选节点连接字符串。并非真正连接，不会Open
        /// </summary>
        /// <param name="connection">待打开的数据库连接。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步操作。</returns>
        private Task OpenConnectionAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            if (connection == null)
            {
                throw new InvalidOperationException("数据库连接不能为空。");
            }

            if (ConnectionRoutingState.IsMaster(connection))
            {
                // 如果连接已经标记为主库，则直接使用主库连接字符串，不再尝试从库。
                SetConnectionString(connection, _nodes[_nodes.Length - 1].ConnectionString);
                return Task.CompletedTask;
            }

            var node = GetCandidates().FirstOrDefault();
            if (node == null)
            {
                throw new InvalidOperationException("没有可用的数据库节点。");
            }

            SetConnectionString(connection, node.ConnectionString);
            MarkRoutingState(connection, node);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 根据候选节点更新连接的主从路由状态。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <param name="node">当前选中的节点。</param>
        private static void MarkRoutingState(DbConnection connection, ReadNode node)
        {
            if (node.IsMaster)
            {
                ConnectionRoutingState.MarkMaster(connection);
            }
            else
            {
                ConnectionRoutingState.MarkRead(connection);
            }
        }

        /// <summary>
        /// 获取当前请求的连接候选顺序。
        /// </summary>
        /// <returns>按优先级排列的节点列表。</returns>
        private IReadOnlyList<ReadNode> GetCandidates()
        {
            var slaves = _nodes.Where(node => !node.IsMaster).ToArray();    // 排除主库节点，获取从库节点列表
            var availableNodes = new List<ReadNode>();
            var availableSlaves = slaves
                .Where(node => !ConnectionCoolingState.IsCooling(node.ConnectionString))        // 排除冷却状态的从库节点
                .ToArray();

            if (availableSlaves.Length > 0)
            {
                var startIndex = GetWeightedStartIndex(availableSlaves);
                for (var offset = 0; offset < availableSlaves.Length; offset++)
                {
                    availableNodes.Add(availableSlaves[(startIndex + offset) % availableSlaves.Length]);
                }
            }

            var master = _nodes[_nodes.Length - 1];
            if (!ConnectionCoolingState.IsCooling(master.ConnectionString))
            {
                availableNodes.Add(master);     // 非冷却，将主库节点添加到候选列表的末尾
            }

            if (availableNodes.Count > 0)
            {
                // 返回自认为健康可用的节点列表，按加权轮询顺序排列。
                return availableNodes;
            }

            // 忽略冷却状态，返回所有节点列表，按加权轮询顺序排列。
            var allNodes = new List<ReadNode>();
            var forcedStartIndex = GetWeightedStartIndex(slaves);
            for (var offset = 0; offset < slaves.Length; offset++)
            {
                allNodes.Add(slaves[(forcedStartIndex + offset) % slaves.Length]);
            }

            allNodes.Add(master);
            // 返回当前所有节点列表，按加权轮询顺序排列，忽略冷却状态。
            return allNodes;
        }

        /// <summary>
        /// 根据从库配置顺序计算加权轮询起点。
        /// </summary>
        /// <param name="slaves">按配置顺序排列的从库。</param>
        /// <returns>本轮优先尝试的从库索引。</returns>
        private static int GetWeightedStartIndex(IReadOnlyList<ReadNode> slaves)
        {
            if (slaves.Count == 0)
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

            var totalWeight = slaves.Count * (slaves.Count + 1) / 2;
            // 没有意外的话，每次请求进来都会递增 _nextWeightedSlot，保证轮询
            var slot = (Interlocked.Increment(ref _nextWeightedSlot) & int.MaxValue)
                % totalWeight;

            for (var index = 0; index < slaves.Count; index++)
            {
                var currentWeight = slaves.Count - index;
                if (slot < currentWeight)
                {
                    return index;
                }

                slot -= currentWeight;
            }

            return slaves.Count - 1;
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
        /// 校验主库、从库和冷却时间配置。
        /// </summary>
        /// <param name="masterConnectionString">主库连接字符串。</param>
        private void ValidateConfiguration(string masterConnectionString)
        {
            if (string.IsNullOrWhiteSpace(masterConnectionString))
            {
                throw new InvalidOperationException("MasterDb 主库连接字符串不能为空。");
            }

            if (_cooldown <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("读节点冷却时间必须大于零。");
            }

            if (_nodes.Any(node => string.IsNullOrWhiteSpace(node.ConnectionString)))
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
