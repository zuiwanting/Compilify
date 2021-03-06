﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Compilify.LanguageServices;
using Compilify.Models;
using MassTransit;
using Newtonsoft.Json;
using NLog;

namespace Compilify.Worker
{
    public sealed class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly ICodeCompiler Compiler = new CSharpCompiler();

        private static readonly ManualResetEvent ResetEvent = new ManualResetEvent(false);

        public static int Main(string[] args)
        {
            Logger.Info("Application started.");

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledApplicationException;

            // Log the exception, but do not mark it as observed. The process will be terminated and restarted 
            // automatically by AppHarbor
            TaskScheduler.UnobservedTaskException +=
                (sender, e) =>
                {
                    Bus.Shutdown();
                    ResetEvent.Set();

                    Logger.ErrorException("An unobserved task exception occurred", e.Exception);
                };

            ConfigureServiceBus();

            ResetEvent.WaitOne();

            Bus.Shutdown();

            return -1;
        }

        private static void ConfigureServiceBus()
        {
            var connectionString = ConfigurationManager.AppSettings["CLOUDAMQP_URL"].Replace("amqp://", "rabbitmq://");
            var queueName = ConfigurationManager.AppSettings["Compilify.WorkerMessagingQueue"];

            var connectionUri = new Uri(connectionString + "/" + queueName);

            var userInfo = connectionUri.UserInfo;

            var queueUri = connectionUri.ToString();
            string username = null;
            string password = null;

            if (!string.IsNullOrEmpty(userInfo))
            {
                queueUri = queueUri.Replace(userInfo + "@", string.Empty);
                var credentials = userInfo.Split(new[] { ':' });
                username = credentials[0];
                password = credentials[1];
            }
            

            var endpointAddress = string.Format("{0}/{1}", connectionString, queueName);

            Bus.Initialize(
                sbc =>
                {
                    sbc.UseRabbitMq(x => x.ConfigureHost(new Uri(queueUri), y =>
                                                                            {
                                                                                if (!string.IsNullOrEmpty(username))
                                                                                {
                                                                                    y.SetUsername(username);
                                                                                }
                                                                                
                                                                                if (!string.IsNullOrEmpty(password))
                                                                                {
                                                                                    y.SetPassword(password);
                                                                                }
                                                                            }));
                    sbc.ReceiveFrom(endpointAddress);
                    sbc.Subscribe(subs => subs.Handler<EvaluateCodeCommand>(ProcessCommand));
                });
        }

        private static void ProcessCommand(EvaluateCodeCommand cmd)
        {
            if (cmd == null)
            {
                return;
            }

            var timeInQueue = DateTime.UtcNow - cmd.Submitted;

            Logger.Info("Job received after {0:N3} seconds in queue.", timeInQueue.TotalSeconds);

            if (timeInQueue > cmd.TimeoutPeriod)
            {
                Logger.Warn("Job was in queue for longer than {0} seconds, skipping!", cmd.TimeoutPeriod.Seconds);
                return;
            }

            var startedOn = DateTime.UtcNow;
            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var assembly = Compiler.Compile(cmd);

            ExecutionResult result;
            if (assembly == null)
            {
                result = new ExecutionResult
                {
                    Result = "[compiling of code failed]"
                };
            }
            else
            {
                using (var executor = new Sandbox())
                {
                    result = executor.Execute(assembly, cmd.TimeoutPeriod);
                }
            }

            stopWatch.Stop();
            var stoppedOn = DateTime.UtcNow;

            Logger.Info("Work completed in {0} milliseconds.", stopWatch.ElapsedMilliseconds);

            try
            {
                var response = new WorkerResult
                {
                    ExecutionId = cmd.ExecutionId,
                    ClientId = cmd.ClientId,
                    StartTime = startedOn,
                    StopTime = stoppedOn,
                    RunDuration = stopWatch.Elapsed,
                    ProcessorTime = result.ProcessorTime,
                    TotalMemoryAllocated = result.TotalMemoryAllocated,
                    ConsoleOutput = result.ConsoleOutput,
                    Result = result.Result
                };

                Bus.Instance.Publish(response);
            }
            catch (JsonSerializationException ex)
            {
                Logger.ErrorException("An error occurred while attempting to serialize the JSON result.", ex);
            }

            stopWatch.Reset();
        }

        public static void OnUnhandledApplicationException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;

            if (e.IsTerminating)
            {
                Logger.FatalException("An unhandled exception is causing the worker to terminate.", exception);
            }
            else
            {
                Logger.ErrorException("An unhandled exception occurred in the worker process.", exception);
            }

            Bus.Shutdown();
            ResetEvent.Set();
        }
    }
}
