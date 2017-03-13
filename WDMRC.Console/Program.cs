﻿using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using NWebDav.Server;
using NWebDav.Server.Http;
using NWebDav.Server.HttpListener;
using NWebDav.Server.Logging;
using YaR.WebDavMailRu.CloudStore;
using YaR.WebDavMailRu.CloudStore.Mailru.StoreBase;

namespace YaR.WebDavMailRu
{
    static class Program
    {
        private static readonly log4net.ILog Logger;

        static Program()
        {
            log4net.Config.XmlConfigurator.Configure();
            Logger = log4net.LogManager.GetLogger(typeof(Program));
        }


        static void Main(string[] args)
        {
            LoggerFactory.Factory = new Log4NetAdapter();

            ShowInfo();

            var result = Parser.Default.ParseArguments<CommandLineOptions>(args);

            var exitCode = result
              .MapResult(
                options => 
                {
                    Cloud.Init(options.UserAgent);

                    var webdavProtocol = "http";
                    var webdavIp = "127.0.0.1";
                    var webdavPort = options.Port;
                    var webdavHost = string.IsNullOrWhiteSpace(options.Host) 
                        ? $"{webdavProtocol}://{webdavIp}" 
                        : options.Host.TrimEnd('/');

                    var cancellationTokenSource = new CancellationTokenSource();
                    var httpListener = new HttpListener();
                    try
                    {
                        httpListener.Prefixes.Add($"{webdavHost}:{webdavPort}/");
                        httpListener.AuthenticationSchemes = AuthenticationSchemes.Basic;
                        httpListener.Start();

                        // Start dispatching requests
                        DispatchHttpRequestsAsync(httpListener, cancellationTokenSource.Token, options.MaxThreadCount);

                        // Wait until somebody presses return
                        Logger.Info($"WebDAV server running at {webdavHost}:{webdavPort}");
                        while (Console.ReadKey().KeyChar != 'x') {}

                    }
                    finally
                    {
                        cancellationTokenSource.Cancel();
                        httpListener.Stop();
                    }
                    return 0;

                },
                errors => 1);

            if (exitCode > 0) Environment.Exit(exitCode);
        }


        private static async void DispatchHttpRequestsAsync(HttpListener httpListener, CancellationToken cancellationToken, int maxThreadCount = Int32.MaxValue)
        {
            // Create a request handler factory that uses basic authentication
            var requestHandlerFactory = new CloudStore.Mailru.RequestHandlerFactory();

            // Create WebDAV dispatcher
            var homeFolder = new MailruStore();
            var webDavDispatcher = new WebDavDispatcher(homeFolder, requestHandlerFactory);

            try
            {
                using (var sem = new SemaphoreSlim(maxThreadCount))
                {
                    var semclo = sem;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var httpListenerContext = await httpListener.GetContextAsync().ConfigureAwait(false);
                        if (httpListenerContext == null)
                            break;

                        //if (httpListenerContext.Request.IsAuthenticated)
                        //    httpContext = new HttpBasicContext(httpListenerContext, i => i.Name == webdavUsername && i.Password == webdavPassword);
                        //else httpContext = new HttpContext(httpListenerContext);

                        HttpListenerBasicIdentity identity = (HttpListenerBasicIdentity)httpListenerContext.User.Identity;
                        IHttpContext httpContext = new HttpBasicContext(httpListenerContext, i => i.Name == identity.Name && i.Password == identity.Password);

                        await semclo.WaitAsync(cancellationToken);

                        // ReSharper disable once UnusedVariable
                        Task tsk = Task
                            .Run(async () =>
                            {
                                try
                                {
                                    await webDavDispatcher.DispatchRequestAsync(httpContext);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error("Exception", ex);
                                }

                            }, cancellationToken)
                            .ContinueWith(t => semclo.Release(), cancellationToken);
                    }
                }
            }
            catch (HttpListenerException excListener)
            {
                if (excListener.ErrorCode != ERROR_OPERATION_ABORTED)
                    throw;
            }
            catch (Exception e)
            {
                Logger.Error("Global exception", e);
            }



        }


        private static void ShowInfo()
        {
            string title = GetAssemblyAttribute<AssemblyTitleAttribute>(a => a.Title);
            string description = GetAssemblyAttribute<AssemblyDescriptionAttribute>(a => a.Description);
            string copyright = GetAssemblyAttribute<AssemblyCopyrightAttribute>(a => a.Copyright);
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            Console.WriteLine($"  {title}: {description}");
            Console.WriteLine($"  v.{version}");
            Console.WriteLine($"  {copyright}");
        }

        private static string GetAssemblyAttribute<T>(Func<T, string> value) where T : Attribute
        {
            T attribute = (T)Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(), typeof(T));
            return value.Invoke(attribute);
        }

        // ReSharper disable once InconsistentNaming
        private const int ERROR_OPERATION_ABORTED = 995;
    }
}
