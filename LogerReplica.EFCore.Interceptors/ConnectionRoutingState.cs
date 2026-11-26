using System;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 保存连接实例当前的主从路由状态，供连接、事务和命令拦截器共享。
    /// </summary>
    internal static class ConnectionRoutingState
    {
        private static readonly ConditionalWeakTable<DbConnection, State> States =
            new ConditionalWeakTable<DbConnection, State>();

        /// <summary>
        /// 判断连接实例是否已经升级到主库。
        /// </summary>
        /// <param name="connection">待检查的数据库连接。</param>
        /// <returns>连接已升级到主库时返回 <see langword="true"/>。</returns>
        internal static bool IsMaster(DbConnection connection)
        {
            return connection != null && States.GetValue(connection, CreateState).IsMaster;
        }

        /// <summary>
        /// 将连接实例标记为主库连接。
        /// </summary>
        /// <param name="connection">已切换到主库的数据库连接。</param>
        internal static void MarkMaster(DbConnection connection)
        {
            if (connection != null)
            {
                States.GetValue(connection, CreateState).IsMaster = true;
            }
        }

        /// <summary>
        /// 将连接实例标记为读库连接。
        /// </summary>
        /// <param name="connection">已切换到读库的数据库连接。</param>
        internal static void MarkRead(DbConnection connection)
        {
            if (connection != null)
            {
                States.GetValue(connection, CreateState).IsMaster = false;
            }
        }

        /// <summary>
        /// 创建连接实例对应的初始路由状态。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <returns>初始为读库状态的路由状态。</returns>
        private static State CreateState(DbConnection connection)
        {
            return new State();
        }

        private sealed class State
        {
            public bool IsMaster;
        }
    }
}
