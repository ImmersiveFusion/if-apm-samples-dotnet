﻿// <copyright file="RmqMessageReceiver.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace Utils.Messaging.RabbitMq
{
    public class RmqMessageReceiver : IDisposable
    {
        private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(RmqMessageReceiver));
        private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

        private readonly ILogger<RmqMessageReceiver> logger;
        private readonly IConnectionMultiplexer _mux;
        private readonly IConnection connection;
        private readonly IModel channel;

        public RmqMessageReceiver(ILogger<RmqMessageReceiver> logger, IConnectionMultiplexer mux)
        {
            this.logger = logger;
            _mux = mux;
            this.connection = RmqRabbitMqHelper.CreateConnection();
            this.channel = RmqRabbitMqHelper.CreateModelAndDeclareTestQueue(this.connection);
        }

        public void Dispose()
        {
            this.channel.Dispose();
            this.connection.Dispose();
        }

        public void StartConsumer()
        {
            RmqRabbitMqHelper.StartConsumer(this.channel, this.ReceiveMessage);
        }

        public void ReceiveMessage(BasicDeliverEventArgs ea)
        {
            // Extract the PropagationContext of the upstream parent from the message headers.
            var parentContext = Propagator.Extract(default, ea.BasicProperties, this.ExtractTraceContextFromBasicProperties);
            Baggage.Current = parentContext.Baggage;

            // Start an activity with a name following the semantic convention of the OpenTelemetry messaging specification.
            // https://github.com/open-telemetry/opentelemetry-specification/blob/master/specification/trace/semantic_conventions/messaging.md#span-name
            var activityName = $"{ea.RoutingKey}";

            using (var activity = ActivitySource.StartActivity(activityName, ActivityKind.Consumer, parentContext.ActivityContext))
            {
                try
                {
                    var message = Encoding.UTF8.GetString(ea.Body.ToArray());

                    new MessageHandler(this.logger, _mux).Handle(message);

                    activity?.SetTag("message", message);

                    // The OpenTelemetry messaging specification defines a number of attributes. These attributes are added here.
                    RmqRabbitMqHelper.AddMessagingTags(activity);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Message processing failed.");
                    activity.SetStatus(Status.Unset.WithDescription(ex.Message));
                    activity.RecordException(ex);
                }
            }
        }

        private IEnumerable<string> ExtractTraceContextFromBasicProperties(IBasicProperties props, string key)
        {
            try
            {
                if (props.Headers.TryGetValue(key, out var value))
                {
                    var bytes = value as byte[];
                    return new[] { Encoding.UTF8.GetString(bytes) };
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to extract trace context: {ex}");
            }

            return Enumerable.Empty<string>();
        }
    }
}
