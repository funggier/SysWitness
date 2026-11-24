namespace SysWitness.Core;

/// <summary>One bounded XML parse for the stored subject and reported parent identity.</summary>
public static class SysmonProcessIdentity
{
    public static (Guid? Subject, Guid? Parent) Read(string xml)
    {
        var fields = WindowsEventXml.ReadFields(xml);
        return (WindowsEventXml.ParseGuid(fields?.GetValueOrDefault("ProcessGuid")),
            WindowsEventXml.ParseGuid(fields?.GetValueOrDefault("ParentProcessGuid")));
    }
}
