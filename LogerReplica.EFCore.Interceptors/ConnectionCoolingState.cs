using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace LogerReplica.EFCore.Interceptors
{
    /// <summary>
    /// 在连接打开拦截器与命令拦截器之间共享失败节点的冷却状态。
    /// </summary>
    internal static class ConnectionCoolingState
    {
        private static readonly ConcurrentDictionary<string, DateTimeOffset> CoolingConnections =
            new ConcurrentDictionary<string, DateTimeOffset>();

        /// <summary>
        /// 将连接字符串对应的节点标记为冷却状态。通常在连接失败后调用此方法，以便在冷却期内避免再次尝试连接该节点。
        /// </summary>
        /// <param name="connectionString">失败节点的连接字符串。</param>
        /// <param name="nodeName">节点名称</param>
        /// <param name="cooldown">节点冷却时长。默认60秒</param>
        internal static void MarkCooling(string connectionString, string nodeName, TimeSpan cooldown)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                CoolingConnections[connectionString] = DateTimeOffset.UtcNow.Add(cooldown);
#if DEBUG
                var originalColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[LogerReplica] 节点={nodeName}, 连接失败， 冷却秒数={cooldown.TotalSeconds};");
                }
                finally
                {
                    Console.ForegroundColor = originalColor;
                }
#endif
            }
        }

        /// <summary>
        /// 判断连接字符串对应的节点是否仍处于冷却状态。
        /// </summary>
        /// <param name="connectionString">待读取节点的连接字符串。</param>
        /// <returns>节点仍在冷却期时返回 <see langword="true"/>。</returns>
        internal static bool IsCooling(string connectionString)
        {
            return CoolingConnections.TryGetValue(connectionString, out var coolingUntil)
                && coolingUntil > DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// 清除连接字符串对应节点的冷却状态。通常在连接成功后调用此方法，以便将节点标记为健康状态。
        /// </summary>
        /// <param name="connectionString">已连接成功节点的连接字符串。</param>
        /// <param name="nodeName">节点名称</param>
        internal static void MarkHealthy(string connectionString, string nodeName)
        {
            CoolingConnections.TryRemove(connectionString, out _);
#if DEBUG
            Console.WriteLine($"[LogerReplica] 节点={nodeName}，DataSource：{(connectionString.Length > 20 ? connectionString.Substring(0, 20) : connectionString)}...");
#endif
        }
    }
}
