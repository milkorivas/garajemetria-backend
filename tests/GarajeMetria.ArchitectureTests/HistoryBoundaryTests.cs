using GarajeMetria.Application.History;

namespace GarajeMetria.ArchitectureTests;

public sealed class HistoryBoundaryTests
{
    [Fact]
    public void HistoryPortsExposeMaterializedProviderIndependentContracts()
    {
        Type[] ports = [typeof(IHistoryQuery), typeof(IHistoryReadSession), typeof(IHistoryAccessAuditStore),
            typeof(IHistoryWorkbookWriter), typeof(IHistoryAdmission), typeof(IHistoryClosureDiscovery)];
        foreach (var port in ports)
            foreach (var method in port.GetMethods())
                foreach (var type in method.GetParameters().Select(x => x.ParameterType).Append(method.ReturnType))
                    Verify(type);
        Assert.DoesNotContain(typeof(HistoryFilter).Assembly.GetReferencedAssemblies(), x =>
            x.Name!.StartsWith("DocumentFormat.OpenXml", StringComparison.Ordinal));
    }
    private static void Verify(Type type)
    {
        var name = type.FullName ?? type.Name;
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", name);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", name);
        Assert.DoesNotContain("DocumentFormat.OpenXml", name);
        Assert.DoesNotContain("Microsoft.AspNetCore", name);
        Assert.DoesNotContain("IQueryable", name);
        foreach (var argument in type.GenericTypeArguments) Verify(argument);
    }
}
