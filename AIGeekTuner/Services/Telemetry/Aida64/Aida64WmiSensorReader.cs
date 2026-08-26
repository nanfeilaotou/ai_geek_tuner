using System.Management;

namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>
    /// 真实 WMI 抓取实现：root\WMI\AIDA64_SensorValues。
    /// 只读查询，绝不修改 AIDA64 配置。属性按实际 schema 容错读取：
    /// ID 与 Value 为官方查询路径必需；Label / Type 缺失时保持 null。
    /// </summary>
    public sealed class Aida64WmiSensorReader : IAida64WmiReader
    {
        private const string WmiScope = @"root\WMI";
        private const string ClassName = "AIDA64_SensorValues";

        public Aida64WmiQueryResult Query()
        {
            try
            {
                var scope = new ManagementScope(WmiScope);
                using var searcher =
                    new ManagementObjectSearcher(scope, new SelectQuery(ClassName));
                using var results = searcher.Get();

                var rows = new List<Aida64SensorRow>();
                foreach (var item in results)
                {
                    using (item)
                    {
                        rows.Add(ReadRow((ManagementBaseObject)item));
                    }
                }

                return Aida64WmiQueryResult.Success(rows);
            }
            catch (ManagementException exception)
                when (exception.ErrorCode is ManagementStatus.InvalidClass
                    or ManagementStatus.NotFound)
            {
                return Aida64WmiQueryResult.Failure(Aida64WmiFailureKind.ClassMissing);
            }
            catch (Exception exception)
            {
                return Aida64WmiQueryResult.Failure(
                    Aida64WmiFailureKind.QueryFailed,
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static Aida64SensorRow ReadRow(ManagementBaseObject item)
        {
            var propertyNames = new HashSet<string>(
                item.Properties.Cast<PropertyData>().Select(property => property.Name),
                StringComparer.Ordinal);

            return new Aida64SensorRow(
                ReadString(item, propertyNames, "ID"),
                ReadString(item, propertyNames, "Label"),
                ReadString(item, propertyNames, "Value"),
                ReadString(item, propertyNames, "Type"));
        }

        private static string? ReadString(
            ManagementBaseObject item,
            ISet<string> propertyNames,
            string propertyName)
        {
            if (!propertyNames.Contains(propertyName))
            {
                return null;
            }

            var value = item[propertyName];
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?
                .Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
