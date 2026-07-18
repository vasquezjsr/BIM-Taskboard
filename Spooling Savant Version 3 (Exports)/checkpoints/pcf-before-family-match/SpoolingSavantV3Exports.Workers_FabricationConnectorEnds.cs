using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>Reads ordered end-connector labels from MEP fabrication parts (Edit Part → Connectors tab).</summary>
    public static class FabricationConnectorEnds
    {
        public const string MissingLabel = "—";

        public static string GetConnectorEndLabel(Connector connector, Document doc)
        {
            if (connector == null)
                return MissingLabel;

            FabricationConfiguration config = null;
            try
            {
                config = doc != null ? FabricationConfiguration.GetFabricationConfiguration(doc) : null;
            }
            catch
            {
            }

            return ResolveFabricationConnectorEndLabel(connector, config);
        }

        /// <summary>
        /// End connectors in Edit Part order (C1, C2, …) via <see cref="Connector.Id"/> — never XYZ-sorted.
        /// </summary>
        public static List<Connector> GetEndConnectorsById(FabricationPart part)
        {
            var list = new List<Connector>();
            if (part?.ConnectorManager?.Connectors == null)
                return list;

            try
            {
                foreach (Connector connector in part.ConnectorManager.Connectors)
                {
                    if (connector == null)
                        continue;

                    try
                    {
                        if (connector.ConnectorType != ConnectorType.End)
                            continue;
                    }
                    catch
                    {
                    }

                    list.Add(connector);
                }
            }
            catch
            {
            }

            if (list.Count < 2)
            {
                list.Clear();
                try
                {
                    foreach (Connector connector in part.ConnectorManager.Connectors)
                    {
                        if (connector != null)
                            list.Add(connector);
                    }
                }
                catch
                {
                }
            }

            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>True when the connector is a female thread / FPT hex end.</summary>
        public static bool IsFemaleThreadFabricationConnector(Connector connector, Document doc)
        {
            string label = GetConnectorEndLabel(connector, doc);
            if (string.IsNullOrWhiteSpace(label) || label == MissingLabel)
                return false;

            string up = label.ToUpperInvariant();
            return up.IndexOf("FPT", StringComparison.Ordinal) >= 0
                || up.IndexOf("FEMALE", StringComparison.Ordinal) >= 0
                || (up.IndexOf("HEX", StringComparison.Ordinal) >= 0 && up.IndexOf("THREAD", StringComparison.Ordinal) >= 0)
                || up.IndexOf("NPT-F", StringComparison.Ordinal) >= 0
                || up.IndexOf("FNPT", StringComparison.Ordinal) >= 0;
        }

        /// <summary>True when the connector is the raised/bolted flange face side (Edit Part → ".Flanged: …").</summary>
        public static bool IsFlangedFabricationConnector(Connector connector, Document doc)
        {
            string label = GetConnectorEndLabel(connector, doc);
            if (string.IsNullOrWhiteSpace(label) || label == MissingLabel)
                return false;

            return label.IndexOf("FLANG", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True for weld, groove, sweat, and similar pipe-join ends — not the dimensionable flange face.</summary>
        public static bool IsPipeJoinFabricationConnector(Connector connector, Document doc)
        {
            string label = GetConnectorEndLabel(connector, doc);
            if (string.IsNullOrWhiteSpace(label) || label == MissingLabel)
                return false;

            return label.IndexOf("WELD", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("GROOV", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("SWEAT", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("SOLDER", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("BRAZ", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void GetConnectorEndLabels(
            FabricationPart part,
            Document doc,
            out string end1,
            out string end2)
        {
            end1 = MissingLabel;
            end2 = MissingLabel;

            if (part == null)
                return;

            FabricationConfiguration config = null;
            try
            {
                config = FabricationConfiguration.GetFabricationConfiguration(doc);
            }
            catch
            {
            }

            var connectors = new List<Connector>();
            try
            {
                ConnectorSet connectorSet = part.ConnectorManager?.Connectors;
                if (connectorSet != null)
                {
                    foreach (Connector connector in connectorSet)
                    {
                        if (connector != null)
                            connectors.Add(connector);
                    }
                }
            }
            catch
            {
            }

            if (connectors.Count == 0)
                return;

            OrderFabricationConnectorsAlongPiece(connectors);

            var labels = new List<string>();
            foreach (Connector connector in connectors)
                labels.Add(ResolveFabricationConnectorEndLabel(connector, config));

            if (labels.Count == 1)
            {
                end1 = labels[0];
                return;
            }

            end1 = labels[0];
            end2 = labels[labels.Count - 1];
        }

        private static void OrderFabricationConnectorsAlongPiece(List<Connector> connectors)
        {
            if (connectors == null || connectors.Count <= 1)
                return;

            Line axis = TryLineThroughFarthestConnectors(connectors);
            if (axis != null)
            {
                try
                {
                    XYZ direction = axis.Direction;
                    if (direction != null)
                    {
                        double length = Math.Sqrt(
                            direction.X * direction.X +
                            direction.Y * direction.Y +
                            direction.Z * direction.Z);
                        if (length > 1e-9)
                        {
                            direction = new XYZ(
                                direction.X / length,
                                direction.Y / length,
                                direction.Z / length);
                            connectors.Sort((a, b) =>
                                a.Origin.DotProduct(direction).CompareTo(b.Origin.DotProduct(direction)));
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            connectors.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        private static Line TryLineThroughFarthestConnectors(IList<Connector> connectors)
        {
            if (connectors == null || connectors.Count < 2)
                return null;

            Connector first = null;
            Connector second = null;
            double farthest = 0.0;
            for (int i = 0; i < connectors.Count; i++)
            {
                for (int j = i + 1; j < connectors.Count; j++)
                {
                    double distance = connectors[i].Origin.DistanceTo(connectors[j].Origin);
                    if (distance > farthest)
                    {
                        farthest = distance;
                        first = connectors[i];
                        second = connectors[j];
                    }
                }
            }

            return first == null || second == null || farthest <= 1e-9
                ? null
                : Line.CreateBound(first.Origin, second.Origin);
        }

        private static string ResolveFabricationConnectorEndLabel(
            Connector connector,
            FabricationConfiguration config)
        {
            if (connector == null)
                return MissingLabel;

            try
            {
                FabricationConnectorInfo info = connector.GetFabricationConnectorInfo();
                if (info != null && info.IsValid())
                {
                    int connectorId = info.BodyConnectorId;
                    if (connectorId <= 0)
                        connectorId = info.DoubleWallConnectorId;

                    if (connectorId > 0 && config != null)
                    {
                        string name = config.GetFabricationConnectorName(connectorId);
                        if (!string.IsNullOrWhiteSpace(name))
                            return name.Trim();
                    }
                }
            }
            catch
            {
            }

            try
            {
                return connector.Shape.ToString();
            }
            catch
            {
                return MissingLabel;
            }
        }
    }
}
