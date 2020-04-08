﻿using System;
using System.Diagnostics;
using System.Linq;
using NServiceBus.Settings;
using OpenTelemetry.Collector;
using OpenTelemetry.Trace;

namespace NServiceBus.Diagnostics.OpenTelemetry.Implementation
{
    internal class ProcessMessageListener : ListenerHandler
    {
        public ProcessMessageListener(string sourceName, Tracer tracer) : base(sourceName, tracer)
        {
        }

        public override void OnStartActivity(Activity activity, object payload)
        {
            ProcessEvent(activity, payload as BeforeProcessMessage);
        }

        public override void OnStopActivity(Activity activity, object payload)
        {
            ProcessEvent(activity, payload as AfterProcessMessage);
        }

        private void ProcessEvent(Activity activity, BeforeProcessMessage payload)
        {
            if (payload == null)
            {
                CollectorEventSource.Log.NullPayload("ProcessMessageListener.OnStartActivity");
                return;
            }

            var settings = payload.Context.Builder.Build<ReadOnlySettings>();

            Tracer.StartActiveSpanFromActivity(settings.LogicalAddress().ToString(), activity, SpanKind.Consumer, out var span);

            if (span.IsRecording)
            {
                span.SetAttribute("messaging.message_id", payload.Context.Message.MessageId);
                span.SetAttribute("messaging.operation", "process");

                span.ApplyContext(settings, payload.Context.MessageHeaders);

                foreach (var header in payload.Context.MessageHeaders.Where(pair => pair.Key.StartsWith("NServiceBus.", StringComparison.OrdinalIgnoreCase)))
                {
                    span.SetAttribute(header.Key, header.Value);
                }
            }
        }

        private void ProcessEvent(Activity activity, AfterProcessMessage payload)
        {
            Tracer.CurrentSpan.End();
        }
    }
}