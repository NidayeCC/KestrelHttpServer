// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public class MaxRequestBodySizeTests
    {
        private const int MaxRequestBodySize = 128;

        [Theory]
        [InlineData(MaxRequestBodySize - 1, 0)]
        [InlineData(MaxRequestBodySize, 0)]
        [InlineData(MaxRequestBodySize - 1, 1)]
        [InlineData(MaxRequestBodySize, 1)]
        [InlineData(MaxRequestBodySize - 1, 2)]
        [InlineData(MaxRequestBodySize, 2)]
        public async Task ServerAcceptsRequestBodyWithinLimit(int requestBodySize, int chunks)
        {
            using (var server = CreateServer(MaxRequestBodySize, async httpContext => await httpContext.Response.WriteAsync("hello, world")))
            {
                using (var connection = new TestConnection(server.Port))
                {
                    await SendRequest(connection, requestBodySize, chunks);

                    await connection.Receive(
                        "HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Transfer-Encoding: chunked",
                        "",
                        "c",
                        "hello, world",
                        "0",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [InlineData(MaxRequestBodySize + 1, 0)]
        [InlineData(MaxRequestBodySize + 1, 1)]
        [InlineData(MaxRequestBodySize + 1, 2)]
        public async Task ServerRejectsRequestBodyExceedingLimit(int requestBodySize, int chunks)
        {
            using (var server = CreateServer(MaxRequestBodySize, async httpContext => await httpContext.Response.WriteAsync("hello, world")))
            {
                using (var connection = new TestConnection(server.Port))
                {
                    await SendRequest(connection, requestBodySize, chunks);

                    await connection.ReceiveForcedEnd(
                        "HTTP/1.1 413 Payload Too Large",
                        "Connection: close",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }
        }

        [Fact]
        public async Task LimitNotEnforcedOnUpgradedConnection()
        {
            var sendBytes = MaxRequestBodySize + 1;

            using (var server = CreateServer(MaxRequestBodySize, async httpContext =>
            {
                var stream = await httpContext.Features.Get<IHttpUpgradeFeature>().UpgradeAsync();

                var received = 0;
                while (received < sendBytes)
                {
                    received += await stream.ReadAsync(new byte[1024], 0, 1024);
                }

                var response = Encoding.ASCII.GetBytes($"{received}");
                await stream.WriteAsync(response, 0, response.Length);
            }))
            {
                using (var connection = new TestConnection(server.Port))
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "Connection: upgrade",
                        "",
                        new string('a', sendBytes));

                    await connection.Receive(
                        "HTTP/1.1 101 Switching Protocols",
                        "Connection: Upgrade",
                        $"Date: {server.Context.DateHeaderValue}",
                        "",
                        $"{sendBytes}");
                }
            }
        }

        private async Task SendRequest(TestConnection connection, int requestBodySize, int chunks)
        {
            await connection.Send(
                "POST / HTTP/1.1",
                "");

            if (chunks == 0)
            {
                await connection.Send(
                    $"Content-Length: {requestBodySize}",
                    "",
                    new string('a', requestBodySize));
            }
            else
            {
                await connection.Send(
                    "Transfer-Encoding: chunked",
                    "",
                    "");

                var bytesSent = 0;
                while (bytesSent < requestBodySize)
                {
                    var chunkSize = Math.Min(requestBodySize / chunks, requestBodySize - bytesSent);

                    await connection.Send(
                        $"{chunkSize:X}",
                        new string('a', chunkSize),
                        "");

                    bytesSent += chunkSize;
                }

                await connection.Send(
                    "0",
                    "",
                    "");
            }
        }

        private TestServer CreateServer(int maxRequestBodySize, RequestDelegate app)
        {
            return new TestServer(app, new TestServiceContext
            {
                ServerOptions = new KestrelServerOptions
                {
                    AddServerHeader = false,
                    Limits =
                    {
                        MaxRequestBodySize = maxRequestBodySize
                    }
                }
            });
        }
    }
}
