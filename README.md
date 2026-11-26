# LogerReplica.EFCore.Interceptors

[LogerReplica.EFCore.Interceptors](https://github.com/logerlink/LogerReplica) 是一个用于 EF Core 读写分离、读节点负载分配和数据库连接故障转移的拦截器包，不依赖 ProxySQL 等外部组件。

本包以 `.NET 5.0` 为目标框架，依赖 EF Core `5.0.17` 的拦截 API。使用其他 EF Core 主版本时，请先确认对应版本的拦截器 API 和依赖兼容性。EF Core 3.1 是最早支持本包所使用连接拦截 API 的版本，但本包当前的编译依赖为 EF Core 5.0.17。

EF Core interceptors for MySQL read/write routing and connection failover. The package targets `.NET 5.0` and is compiled against EF Core `5.0.17`. Compatibility with later EF Core major versions should be verified by the application.

MySQL 已验证可用；SQL Server、PostgreSQL、Oracle 等数据库未经过完整验证，理论上可以工作，但需要调用方自行确认连接字符串、SQL 方言和异常分类行为。

MySQL is verified. SQL Server, PostgreSQL, Oracle, and other databases are not fully tested; callers should verify provider behavior, SQL dialects, and exception classification.

## Installation

通过 NuGet 安装：

```bash
dotnet add package LogerReplica.EFCore.Interceptors --version 5.0.1
```

项目仍需要自行安装对应数据库的 EF Core provider，例如 `Pomelo.EntityFrameworkCore.MySql`。

## Configuration

配置模板

The interceptors read these values from `IConfiguration`:

```json
{
  "ConnectionStrings": {
    "MasterDb": "Server=127.0.0.1;Port=3316;Database=my_school;Uid=root;Pwd=123456;",
    "SlaveDbs": [
      "Server=127.0.0.1;Port=3317;Database=my_school;Uid=root;Pwd=123456;",
      "Server=127.0.0.1;Port=3318;Database=my_school;Uid=root;Pwd=123456;"
    ],
    "ReadNodeCooldownSeconds": 60
  }
}
```
`MasterDb` 必填，且不能是空字符串。`SlaveDbs` 可设置一个或多个只读节点，也可以不配置；不配置时所有操作都使用主库。`ReadNodeCooldownSeconds` 默认值为 60 秒，必须大于 0，表示节点连接失败后进入冷却期，在冷却期内会暂时跳过该节点。

从库按数组顺序参与加权轮询，越靠前的节点默认权重越高。读节点全部不可用时会尝试主库；所有节点都处于冷却状态时，为了避免永久无法恢复，仍会重新尝试全部节点。配置缺少主库、包含空的节点连接字符串或冷却时间不大于 0 时，拦截器初始化会抛出异常。

`MasterDb` is required. `SlaveDbs` is optional; when omitted, all operations use the master. Replicas are weighted by configuration order, with earlier entries receiving higher default weight. Failed nodes are temporarily skipped, and the master is the final read fallback. `ReadNodeCooldownSeconds` defaults to 60 seconds and must be greater than zero. Missing or empty node connection strings cause initialization to fail.

## Registration

如何使用 

Register all three interceptors as singletons so their routing counters and node health state are shared across requests, then attach them to the EF Core options:

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
`ReadConnectionOpeningInterceptor` 负责打开连接时选择节点，`ReadWriteSplittingInterceptor` 负责命令级读写路由，`ReadWriteTransactionInterceptor` 负责在创建事务前切换到主库。三个拦截器必须注册为单例，并且必须全部挂载到同一个 `DbContext` 的 options 上。

拦截器会将非查询命令、事务内查询、写操作后的查询以及依赖连接会话状态的查询路由到主库。普通只读查询从配置的从库中选择，在从库连接失败时按候选顺序切换，并最终回退到主库。`SaveChanges`、`SaveChangesAsync`、`ExecuteSqlRaw` 和 `ExecuteSqlRawAsync` 等写入操作不会自动重试，以避免数据库已提交但应用未收到响应时造成重复写入。

默认的 `UseMySql` 连接字符串仍应指向主库。建议显式指定 `ServerVersion`，避免 `ServerVersion.AutoDetect` 在应用启动或配置阶段主动连接数据库。

The interceptors route non-query commands and transactional or write-following reads to the master. Read connections are selected from the configured replicas, falling back to the master when needed.

## Routing behavior

| 场景 | 路由结果 |
| --- | --- |
| 普通 `SELECT` | 优先使用从库，按配置顺序加权轮询。 |
| 从库连接失败 | 进入冷却期并尝试其他从库；没有可用从库时回退到主库。 |
| `INSERT`、`UPDATE`、`DELETE`、DDL 或其他非查询命令 | 使用主库。 |
| 显式或隐式事务 | 创建事务前切换到主库，事务内命令使用主库。 |
| 写操作后的查询 | 使用主库，保证当前异步调用链中的读一致性。 |
| `LAST_INSERT_ID()`、锁查询等依赖会话或锁状态的查询 | 使用主库。 |

节点健康状态和加权轮询计数保存在进程内。多进程或多实例部署时，每个进程分别维护节点状态，不会自动在实例之间共享。

## Expected behavior

Mysql：主库3316+从库3317+从库3318

写操作、事务、SaveChange获取最新Id等使用主库3316连接，如果主库3316连接异常，则写操作、事务均会失败

读操作优先分配给从库3317，其次是从库3318。如果从库3317连接异常，则优先分配给3318，如果从库3318也出现连接异常，则分配给主库3316兜底(可能会造成主库压力)，如果主库3316也出现连接异常，则报错，无法查询。

报错后，下一次请求会重新开始一轮连接检测；如果节点已经恢复，则会重新参与路由，否则继续尝试其他节点或返回异常。

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
| Query+Delete+Transaction+Update+Insert |   OK   |
|    ExecuteSqlRaw+ExecuteSqlRawAsync    |   OK   |
|          SqlQueryRaw+SqlQuery          |   OK   |

## License

MIT
