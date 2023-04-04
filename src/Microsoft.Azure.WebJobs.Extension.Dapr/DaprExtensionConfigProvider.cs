﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// ------------------------------------------------------------

namespace Microsoft.Azure.WebJobs.Extension.Dapr
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Description;
    using Microsoft.Azure.WebJobs.Host.Config;
    using Microsoft.Azure.WebJobs.Logging;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Defines the configuration options for the Dapr binding.
    /// </summary>
    [Extension("Dapr")]
    class DaprExtensionConfigProvider : IExtensionConfigProvider
    {
        readonly DaprServiceClient daprClient;     // TODO: Use an interface for mocking
        readonly DaprServiceListener daprListener; // TODO: Use an interface for mocking
        readonly INameResolver nameResolver;
        readonly ILogger logger;

        public DaprExtensionConfigProvider(
            DaprServiceClient daprClient,
            DaprServiceListener daprListener,
            ILoggerFactory loggerFactory,
            INameResolver nameResolver)
        {
            this.daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
            this.daprListener = daprListener ?? throw new ArgumentNullException(nameof(daprListener));

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            this.logger = loggerFactory.CreateLogger(LogCategories.CreateTriggerCategory("Dapr"));
            this.nameResolver = nameResolver;
        }

        public void Initialize(ExtensionConfigContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            this.logger.LogInformation($"Registered dapr extension");

            var daprStateConverter = new DaprStateConverter(this.daprClient);

            // NOTE: The order of conversions for each binding rules is important!
            var stateRule = context.AddBindingRule<DaprStateAttribute>();
            stateRule.AddConverter<byte[], DaprStateRecord>(CreateSaveStateParameters);
            stateRule.AddConverter<JsonElement, DaprStateRecord>(CreateSaveStateParameters);
            stateRule.AddConverter<object, DaprStateRecord>(CreateSaveStateParameters);
            stateRule.BindToCollector(attr => new DaprSaveStateAsyncCollector(attr, this.daprClient));
            stateRule.BindToInput<string>(daprStateConverter);
            stateRule.BindToInput<object?>(daprStateConverter);
            stateRule.BindToInput<Stream>(daprStateConverter);
            stateRule.BindToInput<byte[]>(daprStateConverter);

            // TODO: This does not work for nulls and value types. Need a better way of doing this conversion.
            stateRule.BindToInput<object?>(daprStateConverter);

            var invokeRule = context.AddBindingRule<DaprInvokeAttribute>();
            invokeRule.AddConverter<byte[], InvokeMethodParameters>(CreateInvokeMethodParameters);
            invokeRule.AddConverter<JsonElement, InvokeMethodParameters>(CreateInvokeMethodParameters);
            invokeRule.BindToCollector(attr => new DaprInvokeMethodAsyncCollector(attr, this.daprClient));

            var publishRule = context.AddBindingRule<DaprPublishAttribute>();
            publishRule.AddConverter<byte[], DaprPubSubEvent>(CreatePubSubEvent);
            publishRule.AddConverter<JsonElement, DaprPubSubEvent>(CreatePubSubEvent);
            publishRule.AddConverter<object, DaprPubSubEvent>(CreatePubSubEvent);
            publishRule.BindToCollector(attr => new DaprPublishAsyncCollector(attr, this.daprClient));

            var daprBindingRule = context.AddBindingRule<DaprBindingAttribute>();
            daprBindingRule.AddConverter<JsonElement, DaprBindingMessage>(CreateBindingMessage);
            daprBindingRule.AddConverter<byte[], DaprBindingMessage>(CreateBindingMessage);
            daprBindingRule.AddConverter<object, DaprBindingMessage>(CreateBindingMessage);
            daprBindingRule.BindToCollector(attr => new DaprBindingAsyncCollector(attr, this.daprClient));

            var daprSecretConverter = new DaprSecretConverter(this.daprClient);
            var secretsRule = context.AddBindingRule<DaprSecretAttribute>();
            secretsRule.BindToInput<string?>(daprSecretConverter);
            secretsRule.BindToInput<byte[]>(daprSecretConverter);
            secretsRule.BindToInput<object?>(daprSecretConverter);
            secretsRule.BindToInput<IDictionary<string, string>>(daprSecretConverter);

            context.AddBindingRule<DaprServiceInvocationTriggerAttribute>()
                .BindToTrigger(new DaprServiceInvocationTriggerBindingProvider(this.daprListener, this.nameResolver));

            context.AddBindingRule<DaprTopicTriggerAttribute>()
                .BindToTrigger(new DaprTopicTriggerBindingProvider(this.daprListener, this.nameResolver));

            context.AddBindingRule<DaprBindingTriggerAttribute>()
                .BindToTrigger(new DaprBindingTriggerBindingProvider(this.daprListener, this.nameResolver));
        }

        static DaprPubSubEvent CreatePubSubEvent(byte[] arg)
        {
            return CreatePubSubEvent(BytesToJsonElement(arg));
        }

        static DaprPubSubEvent CreatePubSubEvent(object arg)
        {
            return new DaprPubSubEvent(arg);
        }

        static DaprPubSubEvent CreatePubSubEvent(JsonElement json)
        {
            DaprPubSubEvent? event_ = JsonSerializer.Deserialize<DaprPubSubEvent>(json);
            if (event_ == null)
            {
                throw new ArgumentException($"A '{nameof(event_.Payload).ToLowerInvariant()}' parameter is required for outbound pub/sub operations.", nameof(json));
            }

            return event_;
        }

        static JsonElement BytesToJsonElement(byte[] arg)
        {
            string json = Encoding.UTF8.GetString(arg);
            return JsonDocument.Parse(json).RootElement;
        }

        static DaprBindingMessage CreateBindingMessage(object paramValues)
        {
            return new DaprBindingMessage(paramValues);
        }

        static DaprBindingMessage CreateBindingMessage(byte[] paramValues)
        {
            return CreateBindingMessage(BytesToJsonElement(paramValues));
        }

        static DaprBindingMessage CreateBindingMessage(JsonElement jsonElement)
        {
            if (!jsonElement.TryGetProperty("data", out JsonElement data))
            {
                throw new ArgumentException("A 'data' parameter is required for Dapr Binding operations.", nameof(jsonElement));
            }

            DaprBindingMessage message = new DaprBindingMessage(data.Deserialize<object>() ?? throw new InvalidOperationException());

            if (jsonElement.TryGetProperty("operation", out JsonElement operation))
            {
                message.Operation = JsonSerializer.Deserialize<string>(operation);
            }

            if (jsonElement.TryGetProperty("metadata", out JsonElement metadata))
            {
                message.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
            }

            if (jsonElement.TryGetProperty("bindingName", out JsonElement binding))
            {
                message.BindingName = JsonSerializer.Deserialize<string>(binding);
            }

            return message;
        }

        internal static DaprStateRecord CreateSaveStateParameters(byte[] arg)
        {
            return CreateSaveStateParameters(BytesToJsonElement(arg));
        }

        internal static DaprStateRecord CreateSaveStateParameters(JsonElement parametersJson)
        {
            if (!parametersJson.TryGetProperty("value", out JsonElement value))
            {
                throw new ArgumentException("A 'value' parameter is required for save-state operations.", nameof(parametersJson));
            }

            var parameters = new DaprStateRecord(value);

            if (parametersJson.TryGetProperty("key", out JsonElement key))
            {
                parameters.Key = key.GetRawText();
            }

            return parameters;
        }

        internal static DaprStateRecord CreateSaveStateParameters(object parametersValue)
        {
            return new DaprStateRecord(parametersValue);
        }

        internal static InvokeMethodParameters CreateInvokeMethodParameters(byte[] arg)
        {
            return CreateInvokeMethodParameters(arg);
        }

        internal static InvokeMethodParameters CreateInvokeMethodParameters(JsonElement parametersJson)
        {
            var options = new InvokeMethodParameters();

            if (parametersJson.TryGetProperty("appId", out JsonElement appId))
            {
                options.AppId = appId.GetRawText();
            }

            if (parametersJson.TryGetProperty("methodName", out JsonElement methodName))
            {
                options.MethodName = methodName.GetRawText();
            }

            if (parametersJson.TryGetProperty("body", out JsonElement body))
            {
                options.Body = body;
            }

            if (parametersJson.TryGetProperty("httpVerb", out JsonElement httpVerb))
            {
                options.HttpVerb = httpVerb.GetRawText();
            }

            return options;
        }
    }
}