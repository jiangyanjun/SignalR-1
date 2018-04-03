using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.SignalR.Microbenchmarks;

namespace Microsoft.AspNetCore.BenchmarkDotNet.Runner
{
    public partial class Program
    {
        static partial void BeforeMain(string[] args)
        {
            // Put things here to replace the startup logic.
            // Make sure you Environment.Exit if you don't want the main logic to run.

            //var bench = new RedisHubLifetimeManagerBenchmark();

            //bench.ClientCount = 2;
            //bench.GlobalSetup();
            //bench.SendAll().Wait();

            //Environment.Exit(0);
        }
    }
}
