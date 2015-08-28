﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Nito.AsyncEx;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : IMessagePublisher, IDisposable {
        private readonly List<DelayedMessage> _delayedMessages = new List<DelayedMessage>();
        private DateTime? _nextMaintenance = null;
        private CancellationTokenSource _maintenanceCancellationTokenSource;
        private readonly Timer _maintenanceTimer;

        public abstract Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken));

        protected async Task AddDelayedMessageAsync(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException("message");

            using (await _asyncLock.LockAsync()) {
                var sendTime = DateTime.UtcNow.Add(delay);
                _delayedMessages.Add(new DelayedMessage {
                    Message = message,
                    MessageType = messageType,
                    SendTime = sendTime
                });

                ScheduleNextMaintenance(sendTime);
            }
        }

        private void ScheduleNextMaintenance(DateTime value) {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value).Write();
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;

            _maintenanceCancellationTokenSource?.Cancel();
            _maintenanceCancellationTokenSource = new CancellationTokenSource();
            _nextMaintenance = value;
            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            Logger.Trace().Message("Scheduling delayed task: delay={0}", delay).Write();
            Task.Factory.StartNewDelayed(delay, async () => await DoMaintenanceAsync(), _maintenanceCancellationTokenSource.Token);
        }
        
        private readonly AsyncLock _asyncLock = new AsyncLock();

        private async Task DoMaintenanceAsync() {
            Logger.Trace().Message("DoMaintenance").Write();
            if (_delayedMessages == null || _delayedMessages.Count == 0)
                return;

            DateTime nextMessageSendTime = DateTime.MaxValue;
            var now = DateTime.UtcNow;
            var messagesToSend = new List<DelayedMessage>();

            foreach (var message in _delayedMessages) {
                if (message.SendTime <= now)
                    messagesToSend.Add(message);
                else if (message.SendTime < nextMessageSendTime)
                    nextMessageSendTime = message.SendTime;
            }

            ScheduleNextMaintenance(nextMessageSendTime);

            if (messagesToSend.Count == 0)
                return;

            using (await _asyncLock.LockAsync()) {
                foreach (var message in messagesToSend) {
                    Logger.Trace().Message("DoMaintenance Send Delayed: {0}", message.MessageType).Write();
                    _delayedMessages.Remove(message);
                    await PublishAsync(message.MessageType, message.Message);
                }
            }
        }

        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }

        public virtual void Dispose() {
            if (_maintenanceTimer != null)
                _maintenanceTimer.Dispose();
        }
    }
}
