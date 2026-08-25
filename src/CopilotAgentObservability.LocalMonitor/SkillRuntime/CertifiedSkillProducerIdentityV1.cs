namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed record CertifiedSkillProducerIdentityV1(
    string SourceApplicationVersion,
    int ProtocolVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    int RegistryRevision);
