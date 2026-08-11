namespace AgentOrchestrator.CodeQuality;

public sealed class QualityTaxonomyOptions
{
    public const string SectionName = "QualityTaxonomy";

    public bool ObservationWriteEnabled { get; set; }

    public bool ObservationReadEnabled { get; set; }

    public string Provider { get; set; } = CoreQualityTerms.ProducerKinds.Unknown;

    public string RoutePolicyVersion { get; set; } = CoreQualityTerms.ProducerKinds.Unknown;
}
