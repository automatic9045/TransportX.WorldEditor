using System;
using System.Collections.Generic;
using TransportX.Network;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TxLane = TransportX.Scripting.Data.Network.Lane;
using TxLaneLayout = TransportX.Scripting.Data.Network.LaneLayout;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Serializes and parses TransportX <c>&lt;LaneLayout&gt;</c> XML documents
/// via TransportX's <see cref="TxLaneLayout"/> schema DTO.
/// </summary>
public static class LaneLayoutXmlSerializer
{
    public static (LaneLayoutDocument? Model, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string key,
        string relativePath,
        string filePath,
        string xmlText)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(xmlText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        TxLaneLayout? dto = TransportXmlBridge.Deserialize<TxLaneLayout>(filePath, xmlText, diagnostics);
        if (dto is null)
        {
            return (null, diagnostics);
        }

        LaneLayoutDocument model = new LaneLayoutDocument
        {
            Key = key,
            RelativePath = relativePath,
        };

        foreach (TxLane lane in dto.Lanes)
        {
            TransportXmlBridge.AppendErrors(lane.Errors, filePath, diagnostics);
            model.Lanes.Add(new LaneDefinition(
                lane.AllowedTraffic.Value ?? string.Empty,
                (LaneFlowDirections)lane.Directions.Value,
                lane.X.Value,
                lane.Y.Value,
                lane.LeftWidth.Value,
                lane.RightWidth.Value));
        }

        return (model, diagnostics);
    }

    public static string Generate(LaneLayoutDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);

        TxLaneLayout dto = new TxLaneLayout();
        foreach (LaneDefinition lane in model.Lanes)
        {
            TxLane laneDto = new TxLane
            {
                AllowedTraffic = new TransportX.Scripting.Data.XmlValue<string?>(lane.AllowedTrafficKey),
                Directions = new TransportX.Scripting.Data.XmlValue<FlowDirections>((FlowDirections)lane.Directions),
                X = new TransportX.Scripting.Data.XmlValue<float>(lane.X),
                Y = new TransportX.Scripting.Data.XmlValue<float>(lane.Y),
                LeftWidth = new TransportX.Scripting.Data.XmlValue<float>(lane.LeftWidth),
                RightWidth = new TransportX.Scripting.Data.XmlValue<float>(lane.RightWidth),
            };
            dto.Lanes.Add(laneDto);
        }

        return TransportXmlBridge.Serialize(dto);
    }
}
