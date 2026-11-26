# LogerReplica.EFCore.Interceptors

专为 MySQL 读写分离和连接故障转移设计的 EF Core 拦截器，不依赖外部组件。该包目标为 `netstandard2.0`，并使用 EF Core 3.1 的拦截 API，使其适用于使用 EF Core 3.1 或更高版本的应用程序。EF Core 3.1 是最早支持连接拦截 API 的 EF Core 版本。

EF Core interceptors for MySQL read/write routing and connection failover. The package targets `netstandard2.0` and uses EF Core 3.1 interception APIs, making it suitable for applications using EF Core 3.1 or later. EF Core 3.1 is the earliest EF Core line with the connection interception API used here.

MySQL 已测试可行，SqlServer、 PostgreSQL、Oracle 等数据库未测试，理论上也可行

Not Only For MySQL, but also theoretically applicable to SqlServer, PostgreSQL, Oracle, etc., though not tested.

## Configuration

配置模板

The interceptors read these values from `IConfiguration`:

```json
{
  "ConnectionStrings": {
    "MasterDb": "Server=master;Database=app;User ID=app;Password=secret;",
    "SlaveDbs": [
      "Server=replica-1;Database=app;User ID=app;Password=secret;",
      "Server=replica-2;Database=app;User ID=app;Password=secret;"
    ],
    "ReadNodeCooldownSeconds": 60   // 5分钟。连接不可用后，进行冷却，5分钟内不再尝试连接该节点。默认值为60秒。
  }
}
```
`MasterDb` 必填。`SlaveDbs`可设置一个或多个只读节点。自动配置权重，第一个节点权重最高。失败的从库节点将被暂时跳过，主节点作为最终的读取兜底。`ReadNodeCooldownSeconds` 默认值为60秒，表示连接不可用后，进行冷却，60秒内不再尝试连接该节点。

`MasterDb` is required. `SlaveDbs` can contain one or more read nodes. The first configured replica receives the highest default routing weight. Failed nodes are temporarily skipped, with the master kept as the final read fallback. `ReadNodeCooldownSeconds` defaults to 60 seconds, meaning that after a connection failure, the node will be skipped for 60 seconds before retrying.

## Registration

如何使用 

Register both interceptors as singletons so their routing counters and node health state are shared across requests, then attach them to the EF Core options:

```csharp
// 主从模式+读写分离：三个拦截器都必须是单例，节点冷却状态才能跨请求共享。
builder.Services.AddSingleton<ReadWriteSplittingInterceptor>();
builder.Services.AddSingleton<ReadConnectionOpeningInterceptor>();
builder.Services.AddSingleton<ReadWriteTransactionInterceptor>();
// DbContext 的默认地址仍配置为主库，但连接打开前会被 ReadWriteSplittingInterceptor 接管。
// 这样可以保留 Repository/IQueryable，同时在主库断开时先尝试从库。
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var masterConn = builder.Configuration.GetConnectionString("MasterDb");
    var interceptor = serviceProvider.GetRequiredService<ReadWriteSplittingInterceptor>();
    var connectionInterceptor = serviceProvider.GetRequiredService<ReadConnectionOpeningInterceptor>();
    var transactionInterceptor = serviceProvider.GetRequiredService<ReadWriteTransactionInterceptor>();

    // 固定服务端版本，避免应用启动时为了 AutoDetect 主库而提前连接主库。
    options.UseMySql(masterConn, new MySqlServerVersion(new Version(8, 0, 36)))
           .AddInterceptors(interceptor, connectionInterceptor, transactionInterceptor); // 先连接路由，再事务和命令读写路由
});
```
拦截器会将非查询命令和事务或写后读取路由到主节点。读取连接从配置的副本中选择，在需要时回退到主节点。

The interceptors route non-query commands and transactional or write-following reads to the master. Read connections are selected from the configured replicas, falling back to the master when needed.

## 预期效果

Mysql：主库3316+从库3317+从库3318

写操作、事务、SaveChange获取最新Id等使用主库3316连接，如果主库3316连接异常，则写操作、事务均会失败

读操作优先分配给从库3317，其次是从库3318。如果从库3317连接异常，则优先分配给3318，如果从库3318也出现连接异常，则分配给主库3316兜底(可能会造成主库压力)，如果主库3316也出现连接异常，则报错，无法查询。

报错后，用户再次点击查询，会开始尝试新的一轮连接检测，存在可用连接则读取并返回数据，不存在则继续报错。所以请利用好这段时间，将你的数据库恢复吧

## 测试结果

Mysql：主库3316+从库3317+从库3318

|                  操作                  | 是否OK |
| :------------------------------------: | :----: |
|               多次Insert               |   OK   |
|       Query+Delete+Insert+Query        |   OK   |
|       Query+Delete+Update+Insert       |   OK   |
|                 Delete                 |   OK   |
|             Update+Delete              |   OK   |
|                AddRange                |   OK   |
| Query+Delete+Transaction+Update+Insert |        |

## License

MIT
