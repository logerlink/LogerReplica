using System;
using System.Data;
using System.Data.Common;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 判断数据库命令是否必须发送到主库，并识别常见的 SQL 或业务错误。
    /// </summary>
    internal static class DatabaseCommandClassifier
    {
        /// <summary>
        /// 判断当前数据库命令是否必须访问主库。
        /// </summary>
        /// <param name="command">待执行的数据库命令。</param>
        /// <param name="hasWrittenInCurrentScope">当前异步调用链是否已经发生写操作。</param>
        /// <returns>必须访问主库时返回 <see langword="true"/>。</returns>
        internal static bool RequiresMaster(
            DbCommand command,
            bool hasWrittenInCurrentScope)
        {
            if (command == null
                || hasWrittenInCurrentScope
                || command.Transaction != null
                || command.CommandType != CommandType.Text)
            {
                return true;
            }

            var commandText = command.CommandText == null
                ? string.Empty
                : command.CommandText.TrimStart();

            // 非 SELECT 命令可能包含 INSERT、UPDATE、DELETE、MERGE、DDL、SET、CALL 等操作，
            // 统一发送到主库，避免仅凭命令首个关键字漏判写操作。
            if (!commandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 自增值、序列值和会话状态必须从执行写入的主库连接读取。
            if (ContainsAny(commandText,
                "LAST_INSERT_ID(",      // MY   MySQL
                "SCOPE_IDENTITY(",      // SS   SQL Server
                "@@IDENTITY",           // SS
                "IDENT_CURRENT(",       // SS
                "LASTVAL(",             // PG   PostgreSQL
                "CURRVAL(",             // PG
                "NEXTVAL(",             // PG
                "@@ROWCOUNT",           // SS
                "@@TRANCOUNT"))         // SS
            {
                return true;
            }

            // SQL Server 锁提示、PostgreSQL/MySQL 行锁查询和 PostgreSQL advisory lock
            // 都依赖主库上的锁或会话状态，不能路由到普通只读从库。
            return ContainsAny(commandText,
                "FOR UPDATE",           // MY
                "FOR SHARE",            // PG
                "FOR NO KEY UPDATE",    // PG
                "FOR KEY SHARE",        // PG
                "UPDLOCK",              // SS
                "HOLDLOCK",             // SS
                "XLOCK",                // SS
                "TABLOCKX",             // SS
                "SERIALIZABLE",         // SS
                "PG_ADVISORY_LOCK(",    // PG
                "PG_ADVISORY_XACT_LOCK(",   // PG
                "PG_TRY_ADVISORY_LOCK(");   // PG
        }

        /// <summary>
        /// 判断异常是否属于 SQL 语句或业务约束错误，而不是节点不可用。
        /// </summary>
        /// <param name="exception">数据库操作异常。</param>
        /// <returns>属于 SQL 或业务错误时返回 <see langword="true"/>。</returns>
        internal static bool IsSqlOrBusinessFailure(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                var message = current.Message ?? string.Empty;
                if (ContainsAny(message,
                    "syntax error",         // PG
                    "incorrect syntax",     // SS
                    "sql syntax",           // MY
                    "unrecognized token",   // PG
                    "could not serialize access",   // PG
                    "duplicate key",    // PG
                    "duplicate entry",  // MY
                    "primary key",      // PG
                    "unique constraint",    //  SS
                    "unique index",         // SS
                    "foreign key constraint",   //  
                    "check constraint",     // SS
                    "cannot insert null",   //  SS
                    "lock wait timeout",    // MY
                    "lock timeout",         // SS
                    "lock request time out",    // SS
                    "deadlock",                 // SS
                    "could not obtain lock",    // PG
                    "canceling statement due to lock",  // PG
                    "23505",    // PG
                    "23514",    // PG
                    "23503",    // PG
                    "40001",    // PG
                    "55P03",    // PG
                    "40P01",    // PG
                    "1064",     // MY
                    "1062",     // MY
                    "1213",     // MY
                    "1205",     // SS
                    "1222",     // SS
                    "2601",     // SS
                    "2627"))    // SS
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断文本中是否包含任意一个指定片段。
        /// </summary>
        /// <param name="text">待检查文本。</param>
        /// <param name="fragments">待匹配片段。</param>
        /// <returns>包含任意片段时返回 <see langword="true"/>。</returns>
        private static bool ContainsAny(string text, params string[] fragments)
        {
            foreach (var fragment in fragments)
            {
                if (text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
