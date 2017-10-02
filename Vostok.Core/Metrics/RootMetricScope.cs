﻿using System;
using Vostok.Commons;
using Vostok.Flow;

namespace Vostok.Metrics
{
    public class RootMetricScope : IMetricScope
    {
        private readonly IMetricConfiguration configuration;

        public RootMetricScope(IMetricConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public IMetricEventWriter WriteEvent()
        {
            var writer = new AirlockMetricEventWriter(configuration.Airlock, configuration.EventRoutingKey);
            EnrichWithContext(writer);
            EnrichWithHostname(writer);
            return writer;
        }

        public IMetricEventWriter WriteMetric()
        {
            var writer = new AirlockMetricEventWriter(configuration.Airlock, configuration.MetricRoutingKey);
            EnrichWithContext(writer);
            EnrichWithHostname(writer);
            return writer;
        }

        private void EnrichWithContext(IMetricEventWriter writer)
        {
            foreach (var pair in Context.Properties.Current)
            {
                if (configuration.ContextFieldsWhitelist.Contains(pair.Key))
                    writer.SetTag(pair.Key, Convert.ToString(pair.Value));
            }
        }

        private void EnrichWithHostname(IMetricEventWriter writer)
        {
            writer.SetTag("host", HttpClientHostname.Get());
        }
    }
}