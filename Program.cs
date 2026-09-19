using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using Serilog;
using Serilog.Events;
using DicomSCP.Configuration;
using DicomSCP.Services;
using DicomSCP.Repository;
using Microsoft.OpenApi.Models;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Rewrite;
using DicomSCP.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 配置控制台（跨平台支持）
if (Environment.UserInteractive)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows平台禁用快速编辑模式
            var handle = ConsoleHelper.GetStdHandle(-10);
            if (handle != IntPtr.Zero && ConsoleHelper.GetConsoleMode(handle, out uint mode))
            {
                mode &= ~(uint)(0x0040 | 0x0010);
                ConsoleHelper.SetConsoleMode(handle, mode);
            }
        }
        // macOS/Linux 保持默认：Ctrl+C 触发进程退出，不要当成普通输入

    }
    catch
    {
        // 忽略控制台配置错误
    }
}

// 注册编码提供程序
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 注册 DICOM 编码
DicomEncoding.RegisterEncoding("GB2312", "GB2312");

// 大文件上传：显式设置 Kestrel 与 Form 限制（全局配置有时不生效，这里强制生效）
var maxBodySizeBytes = builder.Configuration.GetValue<long>("Kestrel:Limits:MaxRequestBodySize", 524288000);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxBodySizeBytes;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxBodySizeBytes;
});

// 取配置
var settings = builder.Configuration.GetSection("DicomSettings").Get<DicomSettings>() 
    ?? new DicomSettings();
// 获取 Swagger 配置
var swaggerSettings = builder.Configuration.GetSection("Swagger").Get<SwaggerSettings>()
    ?? new SwaggerSettings();

// 配置日志
var logSettings = builder.Configuration
    .GetSection("Logging")
    .Get<LogSettings>() ?? new LogSettings();

// 初始化DICOM日志
DicomLogger.Initialize(logSettings);

// 初始化数据库日志
BaseRepository.ConfigureLogging();

// 配置框架日志
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Warning()  // 只记录警告以上的日志
    .WriteTo.Logger(lc => lc
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:l}{NewLine}",
            restrictedToMinimumLevel: LogEventLevel.Warning
        )
    );

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// 添加日志服务
builder.Services.AddLogging(loggingBuilder =>
    loggingBuilder.AddSerilog(dispose: true));

// 添加服务
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 配置 Swagger：仅在开发环境启用，避免生产环境公开 API 文档
var swaggerEnabled = swaggerSettings.Enabled && builder.Environment.IsDevelopment();
if (swaggerEnabled)
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo 
        { 
            Title = swaggerSettings.Title,
            Version = swaggerSettings.Version,
            Description = swaggerSettings.Description
        });
    });
}

// DICOM服务注册
builder.Services
    .AddFellowOakDicom()
    .AddTranscoderManager<NativeTranscoderManager>();

builder.Services.AddSingleton<DicomRepository>();
builder.Services.AddSingleton<StudyBasicInfoRepository>();
builder.Services.AddSingleton<PrintRepository>();
builder.Services.AddSingleton<StorageCommitmentRepository>();
builder.Services.AddSingleton<MppsRepository>();
builder.Services.AddSingleton<UpsRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<DicomDatasetPersistence>();
builder.Services.AddSingleton<DicomServer>();
builder.Services.AddSingleton<WorklistRepository>();
builder.Services.AddSingleton<IStoreSCU, StoreSCU>();
builder.Services.AddSingleton<IMwlScu, MwlScu>();
builder.Services.AddSingleton<IPrintSCU, PrintSCU>();
builder.Services.AddSingleton<LoginAttemptLimiter>();

// 确保配置服务正确注册
builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection("DicomSettings"));
builder.Services.Configure<QueryRetrieveConfig>(builder.Configuration.GetSection("QueryRetrieveConfig"));
builder.Services.AddScoped<IQueryRetrieveSCU, QueryRetrieveSCU>();
// 注册 Swagger 配置
builder.Services.Configure<SwaggerSettings>(builder.Configuration.GetSection("Swagger"));

builder.Services.AddAuthentication("CustomAuth")
    .AddCookie("CustomAuth", options =>
    {
        options.Cookie.Name = "auth";
        options.LoginPath = "/login.html";
        // cookies过期时间
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;  // 禁用默认的滑动过期
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        //启用最大有效期后下发的是请求的时间戳，不打开就是请求的时间戳+30分钟。
        //如果设置的大于30分钟，则每次请求都会更新过期时间，导致过期时间不准确。
        //options.Cookie.MaxAge = TimeSpan.FromHours(12);

        // 添加验证票据过期处理
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            // 修改验证处理
            OnValidatePrincipal = async context =>
            {
                // 检查是否已过期
                if (context.Properties?.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync("CustomAuth");
                    return;
                }

                // 如果不是 status 接口，手动更新过期时间
                if (!context.HttpContext.Request.Path.StartsWithSegments("/api/dicom/status") && 
                    context.Properties is not null)
                {
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(options.ExpireTimeSpan);
                    context.ShouldRenew = true;
                }
            }
        };
    });

// 添加授权但不设置默认策略
builder.Services.AddAuthorization();

// 配置转发头：默认仅信任环回代理；如部署在反向代理/负载均衡后端，
// 在 appsettings.json 的 ForwardedHeaders:KnownProxies / KnownNetworks 中显式列出可信来源。
// 不再清空 KnownNetworks/KnownProxies，避免任意来源伪造 X-Forwarded-For。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                              Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;

    var forwardedSection = builder.Configuration.GetSection("ForwardedHeaders");
    foreach (var proxy in forwardedSection.GetSection("KnownProxies").Get<string[]>() ?? [])
    {
        if (System.Net.IPAddress.TryParse(proxy, out var ip))
        {
            options.KnownProxies.Add(ip);
        }
    }
    foreach (var network in forwardedSection.GetSection("KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = network.Split('/', 2);
        if (parts.Length == 2 &&
            System.Net.IPAddress.TryParse(parts[0], out var prefix) &&
            int.TryParse(parts[1], out var prefixLength) &&
            prefixLength >= 0)
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }
    }
});

// CORS：默认不开放跨域（同源前端无需 CORS）；仅当显式配置 Cors:AllowedOrigins 时放行指定来源。
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("AppCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// 配置 URL 重写规则
var rewriteOptions = new RewriteOptions()
    //将dicomviewer路径下非直接文件的访问重写到dicomviewer/index.html上，解决spa单应用路由问题
    .AddRewrite(
        @"^dicomviewer/(?!.*\.(js|css|png|jpe?g|gif|ico|svg|woff2?|ttf|otf|eot|map|json|mp[34]|webm|mkv|avi|mov|pdf|docx?|xlsx?|pptx?|zip|rar|tar|gz|7z|ts|sh|bat|py|xml|ya?ml|ini|wasm|aac)).*$", 
        "/dicomviewer/index.html", 
        skipRemainingRules: true
    );

var app = builder.Build();

// 启动时初始化数据库结构（建表 + 字段迁移）
var connectionString = builder.Configuration.GetConnectionString("DicomDb")
    ?? throw new ArgumentException("Missing DicomDb connection string");
var isFirstInitialization = await DatabaseInitializer.InitializeAsync(connectionString);
if (isFirstInitialization)
{
    DicomLogger.Information("Database", "[DB] 数据库表首次初始化完成");
}

var dicomPersistence = app.Services.GetRequiredService<DicomDatasetPersistence>();

// 配置 DICOM（启用跳过验证以兼容部分非标准数据）
new DicomSetupBuilder()
    .SkipValidation()
    .Build();
DicomSetupBuilder.UseServiceProvider(app.Services);

CStoreSCP.Configure(settings, dicomPersistence);

// 启动前重放失败队列：将上次运行落盘的失败入库记录重新入库
await dicomPersistence.TryReplayFailedQueueAsync(settings.StoragePath);

// 启动 DICOM 服务器
var dicomServer = app.Services.GetRequiredService<DicomServer>();
await dicomServer.StartAsync();
app.Lifetime.ApplicationStopping.Register(() => dicomServer.StopAsync().GetAwaiter().GetResult());

// 1. 转发头中间件（最先）
app.UseForwardedHeaders();

// 2. API 日志中间件
app.UseMiddleware<ApiLoggingMiddleware>();

// 3. URL 重写（在静态文件之前）
app.UseRewriter(rewriteOptions);

// 4. 静态文件处理
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html", "login.html" }
});
app.UseStaticFiles();

// 5. Swagger（仅开发环境）
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 6. 路由和认证
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseCors("AppCors");  // CORS 应该在这里

// 7. 认证中间件（保护 API + DICOM 数据端点）
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower();
    var requiresAuth =
        (path?.StartsWith("/api/") == true && !path.StartsWith("/api/auth/login")) ||
        path?.StartsWith("/dicomweb") == true ||
        path?.StartsWith("/wado") == true ||
        path?.StartsWith("/viewer/ohif") == true ||
        path?.StartsWith("/viewer/weasis") == true;

    if (requiresAuth && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = 401;
        return;
    }

    // 默认口令未修改时，除改密/登出/会话查询外的受限端点一律拒绝，
    // 强制用户先完成改密，避免长期使用出厂口令。
    var mustChangePassword = context.User.FindFirst("must_change_pwd")?.Value == "1";
    if (requiresAuth && mustChangePassword)
    {
        var allowedWhileChanging =
            path?.StartsWith("/api/auth/change-password") == true ||
            path?.StartsWith("/api/auth/logout") == true ||
            path?.StartsWith("/api/auth/check-session") == true;

        if (!allowedWhileChanging)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "must_change_password" });
            return;
        }
    }

    await next();
});

// 8. 控制器
app.MapControllers();

// 9. 根路径处理
app.MapGet("/", context =>
{
    if (!context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect("/login.html");
    }
    else
    {
        context.Response.Redirect("/index.html");
    }
    return Task.CompletedTask;
});

// 启动完成后输出管理系统地址
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 从配置读取地址，没有就用默认值
    var httpUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost:5000";
    
    // 提取端口号
    var port = "5000";
    if (httpUrl.Contains(':'))
    {
        var parts = httpUrl.Split(':');
        if (parts.Length >= 3)
        {
            port = parts[^1];
        }
    }
    
    // 获取本地IP地址
    string? localIp = null;
    try
    {
        var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in hostEntry.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                localIp = ip.ToString();
                break;
            }
        }
    }
    catch
    {
        // 忽略获取IP地址的错误
    }
    
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine("   DICOM SCP 服务器启动成功！");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine($"   监听地址: {httpUrl}");
    
    if (!string.IsNullOrEmpty(localIp))
    {
        Console.WriteLine($"   管理系统地址: http://{localIp}:{port}");
    }
    else
    {
        Console.WriteLine($"   管理系统地址: {httpUrl}");
    }
    
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine();
});

app.Run(); 

// Windows控制台API定义
internal static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
} 