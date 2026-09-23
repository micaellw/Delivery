
        private class DecoratingConnectionProxy : DispatchProxy
        {
            public IConnection Target { get; set; } = null!;
            public Func<IModel, IModel>? ModelDecorator { get; set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) return null;
                if (targetMethod.Name == nameof(IConnection.CreateModel) && (args == null || args.Length == 0))
                {
                    var realModel = (IModel)targetMethod.Invoke(Target, args)!;
                    return ModelDecorator != null ? ModelDecorator(realModel) : realModel;
                }
                return targetMethod.Invoke(Target, args);
            }
        }

        private class CrashBeforeAckChannelProxy : DispatchProxy
        {
            public IModel Target { get; set; } = null!;
            public TaskCompletionSource<bool> CrashTriggeredTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) return null;

                if (targetMethod.Name == nameof(IModel.BasicAck))
                {
                    try { Target.Abort(); } catch { }
                    CrashTriggeredTcs.TrySetResult(true);
                    throw new InvalidOperationException(" Simulated worker process crash immediately before BasicAck\);
 }

 if (targetMethod.Name == nameof(IModel.BasicNack) && CrashTriggeredTcs.Task.IsCompleted)
 {
 return null;
 }

 if (targetMethod.Name == nameof(IDisposable.Dispose))
 {
 try { Target.Dispose(); } catch { }
 return null;
 }

 return targetMethod.Invoke(Target, args);
 }
 }

 private static IConnection CreateConnectionProxy(IConnection realConn, Func<IModel, IModel> modelDecorator)
 {
 var proxy = DispatchProxy.Create<IConnection, DecoratingConnectionProxy>();
 var impl = (DecoratingConnectionProxy)(object)proxy;
 impl.Target = realConn;
 impl.ModelDecorator = modelDecorator;
 return proxy;
 }

 private static IModel CreateCrashChannelProxy(IModel realModel, out CrashBeforeAckChannelProxy proxyImpl)
 {
 var proxy = DispatchProxy.Create<IModel, CrashBeforeAckChannelProxy>();
 proxyImpl = (CrashBeforeAckChannelProxy)(object)proxy;
 proxyImpl.Target = realModel;
 return proxy;
 }

 private class CrashBeforeAckWorker : GpsRabbitMqConsumerWorker
 {
 private readonly Func<IConnection, IConnection> _connectionDecorator;

 public CrashBeforeAckWorker(
 IServiceProvider serviceProvider,
 IConfiguration configuration,
 IHostApplicationLifetime appLifetime,
 ILogger<GpsRabbitMqConsumerWorker> logger,
 IConnectionMultiplexer? redis,
 Func<IConnection, IConnection> connectionDecorator)
 : base(serviceProvider, configuration, appLifetime, logger, redis)
 {
 _connectionDecorator = connectionDecorator;
 }

 protected override IConnection CreateConnection(ConnectionFactory factory)
 {
 var realConn = base.CreateConnection(factory);
 return _connectionDecorator(realConn);
 }
 }
