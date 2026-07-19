using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionAdapterRegistryTests
{
    [Fact]
    public void DeletionContracts_ExposeOnlyThePinnedPublicShape()
    {
        var adapter = typeof(IRetentionDeletionAdapter);
        Assert.Equal(new[] { "DeleteAsync", "StoreKind" }, PublicInstanceMemberNames(adapter));
        Assert.Equal(typeof(RetentionStoreKind), adapter.GetProperty("StoreKind")!.PropertyType);
        Assert.Equal(typeof(ValueTask<RetentionAdapterResult>), adapter.GetMethod("DeleteAsync")!.ReturnType);
        Assert.Equal(new[] { typeof(RetentionDeleteContext) }, adapter.GetMethod("DeleteAsync")!.GetParameters().Select(parameter => parameter.ParameterType));

        Assert.Equal(
            new[] { "ItemId", "StoreInstanceId", "StoreKind", "ExpectedRevision", "LeaseOwner", "LeaseGeneration", "SourceIdentity", "PrivateLocator", "IntentCursor", "CancellationToken" },
            ConstructorParameterNames(typeof(RetentionDeleteContext)));
        Assert.Equal(new[] { "SourceItemId", "OwnershipReceipt" }, ConstructorParameterNames(typeof(RetentionSourceIdentity)));
        Assert.Equal(new[] { "OpaqueHandle" }, ConstructorParameterNames(typeof(RetentionPrivateLocatorHandle)));
        Assert.Equal(new[] { "Deleted", "LeaseLost", "TransientFailure", "TerminalFailure" }, Enum.GetNames<RetentionAdapterDisposition>());
    }

    [Fact]
    public void AdapterResults_AllowOnlyPinnedErrorCodeMappings()
    {
        AssertResult(RetentionAdapterResult.Deleted, RetentionAdapterDisposition.Deleted, null);
        AssertResult(RetentionAdapterResult.LeaseLost, RetentionAdapterDisposition.LeaseLost, RetentionErrorCode.LeaseLost);

        foreach (var code in new[] { RetentionErrorCode.DeleteBusy, RetentionErrorCode.DeletePermissionDenied, RetentionErrorCode.DeleteIoFailed })
        {
            AssertResult(RetentionAdapterResult.TransientFailure(code), RetentionAdapterDisposition.TransientFailure, code);
        }

        foreach (var code in new[] { RetentionErrorCode.InvalidIdentity, RetentionErrorCode.OwnershipMismatch, RetentionErrorCode.UnexpectedSourceMissing, RetentionErrorCode.ItemLimitExceeded })
        {
            AssertResult(RetentionAdapterResult.TerminalFailure(code), RetentionAdapterDisposition.TerminalFailure, code);
        }

        var transientCodes = new[] { RetentionErrorCode.DeleteBusy, RetentionErrorCode.DeletePermissionDenied, RetentionErrorCode.DeleteIoFailed };
        var terminalCodes = new[] { RetentionErrorCode.InvalidIdentity, RetentionErrorCode.OwnershipMismatch, RetentionErrorCode.UnexpectedSourceMissing, RetentionErrorCode.ItemLimitExceeded };
        foreach (var code in Enum.GetValues<RetentionErrorCode>())
        {
            if (!transientCodes.Contains(code)) Assert.Throws<ArgumentOutOfRangeException>(() => RetentionAdapterResult.TransientFailure(code));
            if (!terminalCodes.Contains(code)) Assert.Throws<ArgumentOutOfRangeException>(() => RetentionAdapterResult.TerminalFailure(code));
        }

        var resultProperties = typeof(RetentionAdapterResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "Disposition", "ErrorCode" }, resultProperties.Select(property => property.Name).Order());
        Assert.DoesNotContain(resultProperties, property => property.PropertyType == typeof(string) || typeof(Exception).IsAssignableFrom(property.PropertyType) || property.PropertyType == typeof(RetentionPrivateLocatorHandle) || property.PropertyType == typeof(RetentionSourceIdentity));
    }

    [Fact]
    public void Registry_RequiresExactlyOneAdapterForEveryKnownKindAndLooksUpDeterministically()
    {
        var adapters = AllAdapters();
        var registry = new RetentionAdapterRegistry(adapters);

        Assert.Equal(1, registry.CoverageVersion);
        foreach (var adapter in adapters)
        {
            Assert.Same(adapter, registry.Get(adapter.StoreKind));
        }

        Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(adapters.Take(4)));
        Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(adapters.Append(new TestAdapter(RetentionStoreKind.RawRecord))));
        var duplicate = adapters.ToArray();
        duplicate[^1] = new TestAdapter(RetentionStoreKind.RawRecord);
        Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(duplicate));
        var undefined = adapters.ToArray();
        undefined[^1] = new TestAdapter((RetentionStoreKind)999);
        Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(undefined));
        var nullAdapter = adapters.ToArray();
        nullAdapter[^1] = null!;
        Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(nullAdapter));
        Assert.Throws<ArgumentNullException>(() => new RetentionAdapterRegistry(null!));
        var undefinedLookup = Assert.Throws<ArgumentOutOfRangeException>(() => registry.Get((RetentionStoreKind)999));
        Assert.DoesNotContain("999", undefinedLookup.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_CopiesItsInputAndUsesBoundedValidationMessages()
    {
        var adapters = AllAdapters().ToList();
        var registry = new RetentionAdapterRegistry(adapters);
        var original = adapters[0];
        adapters[0] = new TestAdapter(RetentionStoreKind.SessionEventContent);

        Assert.Same(original, registry.Get(RetentionStoreKind.SessionEventContent));

        var exception = Assert.Throws<ArgumentException>(() => new RetentionAdapterRegistry(new IRetentionDeletionAdapter[] { new TestAdapter((RetentionStoreKind)999) }));
        Assert.DoesNotContain("999", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IRetentionDeletionAdapter> AllAdapters() =>
        Enum.GetValues<RetentionStoreKind>().Select(kind => (IRetentionDeletionAdapter)new TestAdapter(kind)).ToArray();

    private static IEnumerable<string> PublicInstanceMemberNames(Type type) => type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Where(member => (member.MemberType is MemberTypes.Method or MemberTypes.Property) && (member is not MethodInfo method || !method.IsSpecialName)).Select(member => member.Name).Order();

    private static IEnumerable<string> ConstructorParameterNames(Type type) => type.GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!);

    private static void AssertResult(RetentionAdapterResult result, RetentionAdapterDisposition disposition, RetentionErrorCode? errorCode)
    {
        Assert.Equal(disposition, result.Disposition);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    private sealed class TestAdapter(RetentionStoreKind storeKind) : IRetentionDeletionAdapter
    {
        public RetentionStoreKind StoreKind => storeKind;
        public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) => ValueTask.FromResult(RetentionAdapterResult.Deleted);
    }
}
