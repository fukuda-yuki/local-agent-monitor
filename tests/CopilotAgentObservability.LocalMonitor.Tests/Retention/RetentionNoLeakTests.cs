namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionNoLeakTests
{
    [Fact]
    public void Claim_ToString_IsSafeTypeName()
    {
        var claim = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeletionClaim
        {
            Fence = new("opaque-item", 7, "owner", 3),
            StoreInstanceId = "source-id",
            StoreKind = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord,
            SourceIdentity = new("raw-source", "receipt-material"),
            PrivateLocator = new("private-path"),
            IntentCursor = 0,
            HasCurrentIntent = false,
            LeaseExpiresAt = DateTimeOffset.UnixEpoch
        };
        Assert.Equal("RetentionDeletionClaim", claim.ToString());
        Assert.DoesNotContain("source", claim.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt", claim.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", claim.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceAndAdapterCarrierToStrings_DoNotExposePrivateTokens()
    {
        var identity = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSourceIdentity("source-id-unsafe", "receipt-unsafe");
        var locator = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionPrivateLocatorHandle("C:\\private\\database.db-wal");
        var context = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteContext("opaque-item", "database-name", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, 1, "owner", 1, identity, locator, 0, CancellationToken.None);
        var forbidden = new[] { "source-id-unsafe", "receipt-unsafe", "database-name", "C:\\private\\database.db-wal", "pragma", "exception" };

        foreach (var value in new object[] { identity, locator, context, CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAdapterResult.TransientFailure(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionErrorCode.DeleteBusy) })
            foreach (var marker in forbidden)
                Assert.DoesNotContain(marker, value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerCarrierToStrings_AreTypeNamesOnly()
    {
        var work = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkReference("item-source", 1, CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkKind.Queued);
        var fence = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteFence("item-source", 1, "owner-receipt", 1);
        Assert.Equal("RetentionWorkReference", work.ToString());
        Assert.Equal("RetentionDeleteFence", fence.ToString());
    }

    [Fact]
    public void RetentionCarriers_DeclareOwnToStringGuards()
    {
        var types = new[]
        {
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkReference),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteFence),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeletionClaim),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteContext),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSourceIdentity),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionPrivateLocatorHandle)
        };
        Assert.All(types, type => Assert.Equal(type, type.GetMethod(nameof(ToString), Type.EmptyTypes)!.DeclaringType));
    }

    [Fact]
    public void ReadGrantPublicationAndLeaseCarriers_DoNotExposeOwnershipMaterial()
    {
        var sourceToken = Enumerable.Repeat((byte)0xcd, 32).ToArray();
        var tokenHex = Convert.ToHexString(sourceToken).ToLowerInvariant();
        var tokenBase64 = Convert.ToBase64String(sourceToken);
        var expiry = new DateTimeOffset(2026, 8, 1, 0, 1, 0, TimeSpan.Zero);
        var grant = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionReadGrant(
            new("store-id", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, "77"),
            "opaque-item",
            9,
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionLeaseKind.Operation,
            "lease-owner",
            4,
            expiry,
            sourceToken);
        var lease = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionReadLease<string>(
            "value",
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionRevisionFence.Create(),
            grant,
            static _ => ValueTask.CompletedTask);

        var surfaces = new List<string>();
        AddPublicReflectionSurface(grant, surfaces);
        AddPublicReflectionSurface(lease, surfaces);
        using (var publication = grant.EnterLeasePublication())
        {
            surfaces.Add(publication.ToString() ?? string.Empty);
            surfaces.Add(publication.LeaseExpiresAt.ToString("O"));
        }
        var member = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationMember(grant, 3);
        surfaces.Add(member.ToString() ?? string.Empty);
        using (var set = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationSet.EnterInOrder(new[] { member }))
            surfaces.Add(set.ToString() ?? string.Empty);

        var duplicate = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionReadGrant(
            new("store-id", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, "77"),
            "opaque-item",
            9,
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionLeaseKind.Operation,
            "lease-owner",
            4,
            expiry.AddMinutes(1),
            sourceToken);
        var duplicateTuple = Assert.Throws<ArgumentException>(() => CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationSet.EnterInOrder(
            new[]
            {
                new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationMember(grant, 3),
                new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationMember(duplicate, 4)
            }));
        surfaces.Add(duplicateTuple.Message);
        var nonStrictOrdinal = Assert.Throws<ArgumentException>(() => CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationSet.EnterInOrder(
            new[]
            {
                new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationMember(grant, 3),
                new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionGrantPublicationMember(duplicate, 3)
            }));
        surfaces.Add(nonStrictOrdinal.Message);

        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(tokenHex, surface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(tokenBase64, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnalysisSdkDirectoryLeaseActivationAndRenewalCarriers_DoNotExposeCapabilityMaterial()
    {
        const string storeInstanceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string itemId = "sdk-private-item-marker";
        const string leaseOwner = "sdk-private-lease-owner-marker";
        var captureId = new string('a', 32);
        var parentLocator = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sdk-private-path-raw-prompt-marker"));
        var childLocator = Path.Combine(parentLocator, captureId);
        var ownerToken = Enumerable.Repeat((byte)0xa1, 32).ToArray();
        var sourceToken = Enumerable.Repeat((byte)0xc3, 32).ToArray();
        var requestedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var requestedAtText = requestedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var ownershipMarker = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryOwnershipMarker.Create(
            storeInstanceId,
            captureId,
            17,
            requestedAtText,
            requestedAt.UtcDateTime.Ticks,
            ownerToken);
        var markerSha256 = System.Security.Cryptography.SHA256.HashData(ownershipMarker);
        var reservation = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryReservation(
            captureId,
            17,
            storeInstanceId,
            parentLocator,
            childLocator,
            ownerToken,
            ownershipMarker,
            markerSha256,
            requestedAtText,
            requestedAt.UtcDateTime.Ticks,
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryPhase.Active,
            11);
        var grant = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionReadGrant(
            new(storeInstanceId, CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.AnalysisSdkDirectory, captureId),
            itemId,
            11,
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionLeaseKind.Operation,
            leaseOwner,
            3,
            requestedAt.AddMinutes(2),
            sourceToken);
        var lease = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryOperationLease(
            grant,
            captureId,
            reservation);
        var active = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryActivationResult.Active(lease);
        var closed = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryActivationResult.Closed;

        Assert.Equal("RetentionAnalysisSdkDirectoryOperationLease", lease.ToString());
        Assert.Equal("RetentionAnalysisSdkDirectoryActivationResult", active.ToString());
        Assert.Equal("RetentionAnalysisSdkDirectoryActivationResult", closed.ToString());
        Assert.Equal(
            new[] { "NotDue", "Renewed", "NonrenewableGrantStillUsable", "LeaseLost", "CatalogBusy" },
            Enum.GetNames<CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOperationRenewalDisposition>());

        AssertNoValueBearingPublicMembers(typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryOperationLease));
        AssertNoValueBearingPublicMembers(typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryActivationResult));
        AssertNoDirectSensitiveStorage(typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryOperationLease));
        AssertNoDirectSensitiveStorage(typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAnalysisSdkDirectoryActivationResult));

        var surfaces = new List<string>();
        AddPublicReflectionSurface(lease, surfaces);
        AddPublicReflectionSurface(active, surfaces);
        AddPublicReflectionSurface(closed, surfaces);
        foreach (var disposition in Enum.GetValues<CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOperationRenewalDisposition>())
            AddPublicReflectionSurface(disposition, surfaces);

        var forbidden = new List<string>
        {
            storeInstanceId,
            itemId,
            leaseOwner,
            captureId,
            parentLocator,
            childLocator,
            "sdk-private-path-raw-prompt-marker"
        };
        foreach (var bytes in new[] { ownerToken, ownershipMarker, markerSha256, sourceToken })
        {
            forbidden.Add(Convert.ToHexString(bytes));
            forbidden.Add(Convert.ToBase64String(bytes));
        }

        foreach (var surface in surfaces)
            foreach (var marker in forbidden)
                Assert.DoesNotContain(marker, surface, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqliteDeletionBridgeCarriers_DoNotExposeBoundTokenOrCheckpointData()
    {
        var token = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var grant = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSqliteDeletionGrant(
            new("source-id", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, "7"), token);
        var forbidden = Convert.ToHexString(token).ToLowerInvariant();

        Assert.False(
            (grant.ToString() ?? string.Empty).Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "Ownership material reached a deletion carrier.");
        Assert.DoesNotContain("source-id", grant.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAdapterResult
                .TransientFailure(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionErrorCode.DeleteIoFailed)
                .ToString()
                .Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "Ownership material reached an adapter result.");
    }

    private static void AddPublicReflectionSurface(object value, ICollection<string> surfaces)
    {
        surfaces.Add(value.ToString() ?? string.Empty);
        var type = value.GetType();
        const System.Reflection.BindingFlags publicInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        foreach (var property in type.GetProperties(publicInstance).Where(static property => property.GetIndexParameters().Length == 0))
        {
            surfaces.Add(property.Name);
            AddReflectedValue(property.GetValue(value), surfaces);
        }
        foreach (var field in type.GetFields(publicInstance))
        {
            surfaces.Add(field.Name);
            AddReflectedValue(field.GetValue(value), surfaces);
        }
        if (!type.IsEnum) return;
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            surfaces.Add(field.Name);
            AddReflectedValue(field.GetValue(null), surfaces);
        }
    }

    private static void AddReflectedValue(object? value, ICollection<string> surfaces)
    {
        if (value is byte[] bytes)
        {
            surfaces.Add(Convert.ToHexString(bytes));
            surfaces.Add(Convert.ToBase64String(bytes));
            return;
        }

        surfaces.Add(value?.ToString() ?? string.Empty);
    }

    private static void AssertNoValueBearingPublicMembers(Type type)
    {
        const System.Reflection.BindingFlags publicDeclaredInstance =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;
        Assert.Empty(type.GetConstructors(publicDeclaredInstance));
        Assert.Empty(type.GetProperties(publicDeclaredInstance));
        Assert.Empty(type.GetFields(publicDeclaredInstance));
        Assert.Equal(
            new[] { nameof(ToString) },
            type.GetMethods(publicDeclaredInstance)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name));
    }

    private static void AssertNoDirectSensitiveStorage(Type type)
    {
        const System.Reflection.BindingFlags declaredInstance =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;
        var members = type.GetFields(declaredInstance)
            .Select(static field => (Name: field.Name, MemberType: field.FieldType))
            .Concat(type.GetProperties(declaredInstance).Select(static property => (Name: property.Name, MemberType: property.PropertyType)))
            .ToArray();

        Assert.DoesNotContain(members, static member => member.MemberType == typeof(byte[]));
        Assert.DoesNotContain(
            members,
            static member => new[] { "raw", "content", "path", "locator", "token", "marker", "credential", "secret" }
                .Any(term => member.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
