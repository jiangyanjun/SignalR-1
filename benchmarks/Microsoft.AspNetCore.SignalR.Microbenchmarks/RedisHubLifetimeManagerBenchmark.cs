// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.SignalR.Redis;
using Microsoft.AspNetCore.SignalR.Redis.Tests;
using Microsoft.AspNetCore.SignalR.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.SignalR.Microbenchmarks
{
    public class RedisHubLifetimeManagerBenchmark
    {
        private RedisHubLifetimeManager<TestHub> _manager1;
        private RedisHubLifetimeManager<TestHub> _manager2;
        private TestClient _client1;
        private TestClient _client2;
        private object[] _args;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var server = new TestRedisServer();
            var logger = NullLogger<RedisHubLifetimeManager<TestHub>>.Instance;
            var options = Options.Create(new RedisOptions()
            {
                Factory = t => new TestConnectionMultiplexer(server)
            });

            _manager1 = new RedisHubLifetimeManager<TestHub>(logger, options);
            _manager2 = new RedisHubLifetimeManager<TestHub>(logger, options);

            // Connect clients
            _client1 = new TestClient();
            _manager1.OnConnectedAsync(HubConnectionContextUtils.Create(_client1.Connection)).Wait();
            _client2 = new TestClient();
            _manager2.OnConnectedAsync(HubConnectionContextUtils.Create(_client2.Connection)).Wait();

            _args = new object[] { "Foo" };
        }

        [Benchmark]
        public void SendAll()
        {
            // Send from manager 1 and wait for the client connected to manager 2 to get it
            var send = _manager1.SendAllAsync("Test", _args);
            var read = _client2.ReadAsync();

            Task.WaitAll(send, read);
        }

        public class TestHub : Hub
        {
        }
    }
}
