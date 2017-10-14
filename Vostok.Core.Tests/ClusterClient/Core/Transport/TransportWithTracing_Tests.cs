﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Vostok.Airlock;
using Vostok.Clusterclient.Model;
using Vostok.Clusterclient.Transport;
using Vostok.Tracing;
using NUnit.Framework;

namespace Vostok.Clusterclient.Core.Transport
{
    public class TransportWithTracing_Tests
    {
        private IAirlockClient airlockClient;
        private ITransport transport;
        private TransportWithTracing transportWithTracing;

        [SetUp]
        public void SetUp()
        {
            airlockClient = Substitute.For<IAirlockClient>();
            transport = Substitute.For<ITransport>();
            transportWithTracing = new TransportWithTracing(transport);
            Trace.Configuration.AirlockClient = airlockClient;
            Trace.Configuration.AirlockRoutingKey = () => "routingKey";
        }

        [Test]
        public async Task SendAsync_should_create_trace()
        {
            var request = new Request("POST", new Uri("http://vostok/process?p=p1"),new Content(new byte[10]));
            var timeout = TimeSpan.FromHours(1);
            var cancellationToken = new CancellationToken();
            var response = new Response(ResponseCode.BadRequest);
            transport.SendAsync(request, timeout, cancellationToken).Returns(response);
            var expectedAnnotations = new Dictionary<string, string>
            {
                [TracingAnnotationNames.Kind] = "http-client",
                [TracingAnnotationNames.Component] = "cluster-client",
                [TracingAnnotationNames.HttpUrl] = "http://vostok/process",
                [TracingAnnotationNames.HttpMethod] = "POST",
                [TracingAnnotationNames.HttpRequestContentLength] = "10",
                [TracingAnnotationNames.HttpResponseContentLength] = "0",
                [TracingAnnotationNames.HttpCode] = "400"
            };
            airlockClient.Push(Arg.Any<string>(), Arg.Do<Span>(span =>
            {
                span.Annotations.ShouldBeEquivalentTo(expectedAnnotations);
            }));

            var actual = await transportWithTracing.SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);

            actual.Should().Be(response);
            airlockClient.Received().Push(Arg.Any<string>(), Arg.Any<Span>());
        }
    }
}