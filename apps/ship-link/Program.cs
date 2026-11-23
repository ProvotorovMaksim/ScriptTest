#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusProducer;

namespace SpaceEngineers.Apps.ShipLink
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int DisplayUpdateIntervalTicks = 60;

        private string _shipName;
        private string _baseName = "Base";
        private bool _connectedToBase;
        private int _lastMessageTick;
        private string _lastEventType;
        private string _lastPayload;
        private string _activeTaskId;
        private string _activeTaskStage;
        private string _activeTaskDetails;
        private int _tick;
        private int _displayTick;
        private IMyBroadcastListener _listener;
        private TaskBusProducer _producer;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly List<IMyCockpit> _cockpits = new List<IMyCockpit>();
        private readonly List<IMyTextSurface> _surfaces = new List<IMyTextSurface>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
            _shipName = Me.CubeGrid.CustomName;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _producer = new TaskBusProducer(CommunicationTag, (channel, envelope, targetId) =>
                IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax));
        }

        public void Main(string argument)
        {
            _tick++;
            _displayTick++;

            var command = (argument ?? string.Empty).Trim();
            if (command.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
                _baseName = command.Substring(5).Trim();

            // Scan antennas
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);

            // Scan cockpits and collect text surfaces
            _cockpits.Clear();
            _surfaces.Clear();
            GridTerminalSystem.GetBlocksOfType(_cockpits, cockpit => cockpit.IsFunctional);
            foreach (var cockpit in _cockpits)
            {
                for (int i = 0; i < cockpit.SurfaceCount; i++)
                {
                    var surface = cockpit.GetSurface(i);
                    if (surface.ContentType == ContentType.TEXT_AND_IMAGE ||
                        surface.ContentType == ContentType.TEXT_AND_IMAGE)
                    {
                        _surfaces.Add(surface);
                    }
                }
            }

            // Process IGC messages
            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source == Me.EntityId)
                    continue;

                var json = message.Data as string;
                string eventType;
                string payloadJson;
                if (string.IsNullOrEmpty(json) || !TaskBusIgcHelper.TryParseEnvelope(json, out eventType, out payloadJson))
                    continue;

                _connectedToBase = true;
                _lastMessageTick = _tick;
                _lastEventType = eventType;
                _lastPayload = payloadJson;

                if (eventType == "TASK_ASSIGN")
                {
                    ParseTaskAssignment(payloadJson);
                }
                else if (eventType == "TASK_DONE_ACK")
                {
                    _activeTaskId = null;
                    _activeTaskStage = null;
                    _activeTaskDetails = null;
                }
                else if (eventType == "TASK_REVOKED")
                {
                    _activeTaskId = null;
                    _activeTaskStage = null;
                    _activeTaskDetails = null;
                }
                else if (eventType == "QUEUE_EMPTY")
                {
                    if (string.IsNullOrEmpty(_activeTaskId))
                    {
                        _lastEventType = "QUEUE_EMPTY";
                    }
                }
            }

            // Connection timeout: if no message for 300 ticks (~5 seconds at Update1)
            if (_tick - _lastMessageTick > 300)
            {
                _connectedToBase = false;
            }

            // Update display periodically
            if (_displayTick >= DisplayUpdateIntervalTicks)
            {
                _displayTick = 0;
                UpdateCockpitDisplay();
            }
        }

        private void ParseTaskAssignment(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) &&
                TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId))
            {
                _activeTaskId = taskId;
            }

            string detailsJson;
            string details;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "details", out detailsJson) &&
                TaskBusIgcHelper.TryReadJsonString(detailsJson, out details))
            {
                _activeTaskDetails = details;
            }

            // Try to extract stage from details if it's a JSON with steps
            string stepsJson;
            List<string> steps;
            if (TaskBusProtocol.TryGetProperty(detailsJson, "steps", out stepsJson) &&
                TaskBusProtocol.TryReadStringArray(stepsJson, out steps) &&
                steps.Count > 0)
            {
                _activeTaskStage = steps[0];
            }
            else
            {
                _activeTaskStage = "execute";
            }
        }

        private void UpdateCockpitDisplay()
        {
            if (_surfaces.Count == 0)
                return;

            var display = new StringBuilder();
            display.AppendLine("=== SHIP-LINK STATUS ===");
            display.AppendLine($"Ship: {_shipName}");
            display.AppendLine($"Base: {_baseName}");
            display.AppendLine();

            // Connection status
            if (_connectedToBase)
            {
                display.AppendLine("Connection: ONLINE");
                display.AppendLine($"Last event: {_lastEventType}");
                display.AppendLine($"Ticks since last msg: {_tick - _lastMessageTick}");
            }
            else
            {
                display.AppendLine("Connection: OFFLINE");
                display.AppendLine("No IGC signal from base.");
            }

            display.AppendLine();

            // Antenna status
            var functionalAntennas = _antennas.Count(a => a.IsFunctional && a.Enabled);
            display.AppendLine($"Antennas: {functionalAntennas} active");

            display.AppendLine();

            // Active task
            if (!string.IsNullOrEmpty(_activeTaskId))
            {
                display.AppendLine("=== ACTIVE TASK ===");
                display.AppendLine($"Task ID: {_activeTaskId}");
                display.AppendLine($"Stage: {_activeTaskStage}");
                if (!string.IsNullOrEmpty(_activeTaskDetails))
                {
                    display.AppendLine($"Details: {_activeTaskDetails}");
                }
            }
            else
            {
                display.AppendLine("=== ACTIVE TASK ===");
                display.AppendLine("No active task.");
                display.AppendLine("Waiting for TASK_ASSIGN from base...");
            }

            display.AppendLine();
            display.AppendLine($"Tick: {_tick}");

            var text = display.ToString();
            foreach (var surface in _surfaces)
            {
                surface.ContentType = ContentType.TEXT_AND_IMAGE;
                surface.Font = "Monospace";
                surface.FontSize = 0.8f;
                surface.Alignment = TextAlignment.LEFT;
                surface.WriteText(text);
            }
        }

        #region PreludeFooter
    }
}
#endregion