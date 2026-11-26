using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 在 EF Core 创建事务前，将连接切换到主库。SavaChanges、SaveChangesAsync、ExecuteSqlRaw、ExecuteSqlRawAsync 等方法在执行时会创建隐式事务，因此在这些方法执行前会自动切换到主库。
    /// </summary>
    public sealed class ReadWriteTransactionInterceptor : DbTransactionInterceptor
    {
        private readonly ReadWriteSplittingInterceptor _commandInterceptor;

        /// <summary>
        /// 初始化事务路由拦截器。
        /// </summary>
        /// <param name="commandInterceptor">负责实际主库切换的命令拦截器。</param>
        public ReadWriteTransactionInterceptor(ReadWriteSplittingInterceptor commandInterceptor)
        {
            _commandInterceptor = commandInterceptor
                ?? throw new ArgumentNullException(nameof(commandInterceptor));
        }

        /// <summary>
        /// 在同步事务创建前切换到主库。
        /// </summary>
        /// <param name="connection">即将创建事务的数据库连接。</param>
        /// <param name="eventData">事务创建事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <returns>原始事务创建结果。</returns>
        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result)
        {
            _commandInterceptor.OpenMasterConnectionForRouting(connection);
            return base.TransactionStarting(connection, eventData, result);
        }

        /// <summary>
        /// 在异步事务创建前切换到主库。
        /// </summary>
        /// <param name="connection">即将创建事务的数据库连接。</param>
        /// <param name="eventData">事务创建事件数据。</param>
        /// <param name="result">默认拦截结果。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>异步事务创建结果。</returns>
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await _commandInterceptor.OpenMasterConnectionForRoutingAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);

            return await base.TransactionStartingAsync(
                    connection,
                    eventData,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
