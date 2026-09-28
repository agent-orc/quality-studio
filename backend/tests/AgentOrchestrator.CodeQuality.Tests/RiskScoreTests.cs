namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RiskScoreTests
{
    [Fact]
    public void Complexity_takes_a_fifth_of_the_score_when_it_is_measured()
    {
        // (100-70)*0.3 + (100-40)*0.3 + 50*0.2 + 5*20/10 = 9 + 18 + 10 + 10
        Assert.Equal(47m, RiskScore.Combine(70, 40m, 50, 5, 10));
    }

    [Fact]
    public void An_unmeasured_file_keeps_the_grade_coverage_churn_weights()
    {
        // (100-70)*0.4 + (100-40)*0.4 + 5*20/10 = 12 + 24 + 10
        Assert.Equal(46m, RiskScore.Combine(70, 40m, null, 5, 10));
    }

    [Fact]
    public void Missing_grade_or_coverage_keeps_the_score_unknown_whatever_the_complexity()
    {
        Assert.Null(RiskScore.Combine(null, 40m, 100, 5, 10));
        Assert.Null(RiskScore.Combine(70, null, 100, 5, 10));
    }

    [Fact]
    public void The_worst_file_scores_one_hundred()
    {
        Assert.Equal(100m, RiskScore.Combine(0, 0m, 100, 10, 10));
        Assert.Equal(100m, RiskScore.Combine(0, 0m, null, 10, 10));
    }
}
