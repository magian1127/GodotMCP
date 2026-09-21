using System.Text;

namespace GodotMcp.Daemon;

/// <summary>
/// 极简文件日志:追加写状态目录下的 daemon.log(FileShare.Read,可被 tail 跟随)。
/// 与 stderr 控制台日志并行;写文件失败时静默丢弃 —— 日志绝不影响协议通道。
/// 不做轮转:daemon 空闲即退出,日志体量由活动量自然约束。
/// </summary>
public sealed class StateLogFileLoggerProvider : ILoggerProvider
{
    /// <summary>状态目录下的日志文件名,Program 以 StateDir 拼接后传入构造函数。</summary>
    public const string LogFileName = "daemon.log";

    /// <summary>追加写日志文件的写入器,AutoFlush 保证 tail 跟随时即时可见;跨 logger 实例以 lock 串行化。</summary>
    private readonly StreamWriter _writer;

    /// <summary>
    /// 打开(必要时创建)日志文件并建立追加写入器;由 Program 在 daemon 启动期注册到日志管线。
    /// </summary>
    /// <param name="logPath">日志文件完整路径(状态目录下 daemon.log)。</param>
    /// <exception cref="IOException">目录/文件创建失败时由框架抛出 —— 启动期按环境故障归因。</exception>
    public StateLogFileLoggerProvider(string logPath)
    {
        // logPath 无目录成分(裸文件名)时 GetDirectoryName 为空串 —— 直接建目录会抛。
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _writer = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    /// <summary>
    /// 为指定类别创建文件日志器;每个 ILogger 类别各得一个实例,共享同一写入器。
    /// </summary>
    /// <param name="categoryName">日志类别名(通常是全限定类型名)。</param>
    /// <returns>写入 daemon.log 的 <see cref="ILogger"/> 实例。</returns>
    public ILogger CreateLogger(string categoryName) => new StateFileLogger(this, categoryName);

    /// <summary>关闭并释放日志文件句柄;宿主关机时由日志管线调用。</summary>
    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// 实际写文件的日志器:仅记录 Information 及以上级别,行格式为
    /// "本地时间 级别 类别: 消息",异常另行缩进追加。
    /// </summary>
    /// <param name="owner">所属 provider,借其写入器落盘。</param>
    /// <param name="category">日志类别名,原样写入每行。</param>
    private sealed class StateFileLogger(StateLogFileLoggerProvider owner, string category) : ILogger
    {
        /// <summary>不支持日志作用域,恒返回 null。</summary>
        /// <typeparam name="TState">作用域状态类型。</typeparam>
        /// <param name="state">作用域状态,忽略。</param>
        /// <returns>恒为 null。</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>仅启用 Information 及以上级别,与 stderr 控制台阈值分开控制。</summary>
        /// <param name="logLevel">待判定的日志级别。</param>
        /// <returns>级别不低于 Information 时为 true。</returns>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        /// <summary>
        /// 格式化并追加一行日志到 daemon.log。
        /// </summary>
        /// <typeparam name="TState">格式化状态类型。</typeparam>
        /// <param name="logLevel">日志级别。</param>
        /// <param name="eventId">事件 id,仅满足接口签名,不落盘。</param>
        /// <param name="state">格式化状态。</param>
        /// <param name="exception">关联异常,可为 null。</param>
        /// <param name="formatter">消息格式化器。</param>
        /// <para>逻辑链:级别未启用 → 直接返回 → 拼时间戳/级别/类别/消息,异常非空则追加其完整文本 →
        /// lock(owner._writer) 串行写入并换行 → 写文件抛 IOException(磁盘故障等)时静默丢弃 ——
        /// 日志绝不影响协议通道。</para>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line =
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            try
            {
                lock (owner._writer)
                {
                    owner._writer.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // 磁盘故障等 —— 静默丢弃,日志问题不拖垮 daemon。
            }
        }
    }
}
