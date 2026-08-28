using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

// The #154 registry-generation authority. Publishes one immutable generation behind a
// single current pointer and hands out non-mutating read leases. A publication of a new
// generation blocks until every outstanding lease on the current generation is released,
// so a capability acquired from a generation stays valid for its whole lifetime.
internal sealed class SkillInvocationV2RegistryProviderV1 : ISkillRegistryGenerationAuthority
{
    private readonly object syncRoot = new();
    private readonly HashSet<GenerationLease> outstandingLeases = new();
    private readonly ILocalWorkspacePublicationGate publicationGate;
    private Generation? currentGeneration;

    internal event Action<ISkillRegistryGenerationAuthority>? CurrentGenerationChanging;

    internal SkillInvocationV2RegistryProviderV1()
        : this(SkillInvocationV2ArtifactRegistry.Load(), new LocalWorkspacePublicationGate())
    {
    }

    internal SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry registry)
        : this(registry, new LocalWorkspacePublicationGate())
    {
    }

    internal SkillInvocationV2RegistryProviderV1(
        SkillInvocationV2ArtifactRegistry registry,
        ILocalWorkspacePublicationGate publicationGate)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.publicationGate = publicationGate ?? throw new ArgumentNullException(nameof(publicationGate));
        currentGeneration = new Generation(Guid.NewGuid(), registry);
    }

    public ISkillRegistryGenerationCapture? CaptureGeneration()
    {
        lock (syncRoot)
        {
            var generation = currentGeneration;
            return generation is null ? null : new GenerationCapture(generation);
        }
    }

    public bool TryAcquireGenerationReadLease(
        ISkillRegistryGenerationCapture capture,
        [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
    {
        lease = null;
        if (capture is not GenerationCapture typedCapture)
            return false;

        lock (syncRoot)
        {
            // Pre-lease churn: the current pointer no longer matches the capture, so no lease
            // is granted and the caller must recapture.
            if (!ReferenceEquals(currentGeneration, typedCapture.Generation))
                return false;

            var generationLease = new GenerationLease(this, typedCapture.Generation);
            outstandingLeases.Add(generationLease);
            lease = generationLease;
            return true;
        }
    }

    public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
    {
        if (capture is not GenerationCapture typedCapture || lease is not GenerationLease typedLease)
            return false;

        lock (syncRoot)
        {
            return ReferenceEquals(typedCapture.Generation, typedLease.Generation) &&
                ReferenceEquals(currentGeneration, typedCapture.Generation);
        }
    }

    public string GetCanonicalGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
    {
        if (capture is not GenerationCapture typedCapture || lease is not GenerationLease typedLease
            || !VerifyGenerationIdentity(capture, lease)) throw new InvalidOperationException("skill_registry_generation_not_current");
        return typedCapture.Generation.Identity.ToString("D");
    }

    public string? GetCanonicalArtifactAuthorityIdentity(
        ISkillRegistryGenerationCapture capture,
        ISkillRegistryGenerationLease lease) =>
        capture is GenerationCapture typedCapture && lease is GenerationLease
            && VerifyGenerationIdentity(capture, lease)
            ? typedCapture.Generation.ArtifactAuthorityIdentity
            : null;

    public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
    {
        if (lease is not GenerationLease typedLease)
            return false;

        ArgumentNullException.ThrowIfNull(tuple);
        var compatibilityTuple = new SkillInvocationV2CompatibilityTuple(
            tuple.SourceApplicationVersion,
            tuple.AdapterVersion,
            tuple.NormalizationVersion,
            tuple.PayloadSchema,
            tuple.SchemaFingerprint);
        return typedLease.Generation.Registry.IsAccepted(compatibilityTuple);
    }

    // Publishes a new generation, blocking until every outstanding lease on the current
    // generation is released before swapping the current pointer.
    public void PublishGeneration(SkillInvocationV2ArtifactRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var incoming = new Generation(Guid.NewGuid(), registry);
        var publicationLease = publicationGate.AcquireWriteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        try
        {
            lock (syncRoot)
            {
                while (outstandingLeases.Count > 0)
                {
                    Monitor.Wait(syncRoot);
                }

                CurrentGenerationChanging?.Invoke(new ProposedGenerationAuthority(incoming));
                currentGeneration = incoming;
            }
        }
        finally
        {
            publicationLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class ProposedGenerationAuthority(Generation generation) : ISkillRegistryGenerationAuthority
    {
        public ISkillRegistryGenerationCapture CaptureGeneration() => new GenerationCapture(generation);
        public bool TryAcquireGenerationReadLease(ISkillRegistryGenerationCapture capture, [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            if (capture is not GenerationCapture typed || !ReferenceEquals(typed.Generation, generation)) { lease = null; return false; }
            lease = new ProposedGenerationLease(generation); return true;
        }
        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) =>
            capture is GenerationCapture typedCapture && lease is ProposedGenerationLease typedLease
            && ReferenceEquals(typedCapture.Generation, generation) && ReferenceEquals(typedLease.Generation, generation);
        public string GetCanonicalGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
        {
            if (!VerifyGenerationIdentity(capture, lease)) throw new InvalidOperationException("skill_registry_generation_not_current");
            return generation.Identity.ToString("D");
        }
        public string? GetCanonicalArtifactAuthorityIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) =>
            VerifyGenerationIdentity(capture, lease) ? generation.ArtifactAuthorityIdentity : null;
        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
        {
            if (lease is not ProposedGenerationLease typed || !ReferenceEquals(typed.Generation, generation)) return false;
            ArgumentNullException.ThrowIfNull(tuple);
            return generation.Registry.IsAccepted(new(tuple.SourceApplicationVersion, tuple.AdapterVersion, tuple.NormalizationVersion, tuple.PayloadSchema, tuple.SchemaFingerprint));
        }
    }

    private sealed class ProposedGenerationLease(Generation generation) : ISkillRegistryGenerationLease
    {
        internal Generation Generation { get; } = generation;
        public void Dispose() { }
    }


    internal int OutstandingLeaseCount
    {
        get
        {
            lock (syncRoot)
            {
                return outstandingLeases.Count;
            }
        }
    }

    private void ReleaseLease(GenerationLease lease)
    {
        lock (syncRoot)
        {
            if (outstandingLeases.Remove(lease))
            {
                Monitor.PulseAll(syncRoot);
            }
        }
    }

    private sealed class Generation
    {
        internal Generation(Guid identity, SkillInvocationV2ArtifactRegistry registry)
        {
            Identity = identity;
            Registry = registry;
            var current = registry.History.Single(item => item.Revision == registry.CurrentRevision);
            ArtifactAuthorityIdentity = $"{current.Revision}:{current.ArtifactFingerprint}";
        }

        internal Guid Identity { get; }

        internal string ArtifactAuthorityIdentity { get; }

        internal SkillInvocationV2ArtifactRegistry Registry { get; }
    }

    private sealed class GenerationCapture : ISkillRegistryGenerationCapture
    {
        internal GenerationCapture(Generation generation)
        {
            Generation = generation;
        }

        internal Generation Generation { get; }
    }

    private sealed class GenerationLease : ISkillRegistryGenerationLease
    {
        private readonly SkillInvocationV2RegistryProviderV1 provider;
        private int released;

        internal GenerationLease(SkillInvocationV2RegistryProviderV1 provider, Generation generation)
        {
            this.provider = provider;
            Generation = generation;
        }

        internal Generation Generation { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                provider.ReleaseLease(this);
            }
        }
    }
}
