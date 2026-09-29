namespace Pricing.Domain;

public enum TariffEvaluationFailure
{
    None,
    NoRule,
    AmbiguousRule,
    PolicyVersionMissing,
    TaxModeBlocked,
    ConsolidatedRouteRequired,
}

public sealed record TariffEvaluationContext(
    Guid OwnerOrganizationId,
    Guid CityId,
    Guid? SharedServiceAreaId,
    Guid? SharedOperatingZoneId,
    PricingTier PricingTier,
    ServiceType ServiceType,
    bool ConsolidatedRoute,
    DateTimeOffset EvaluatedAt,
    Guid? PrivateTariffId = null);

public sealed record TariffEvaluationResult(
    TariffEvaluationFailure Failure,
    TariffRule? Rule,
    Money Subtotal,
    Money Discount,
    Money Tax,
    Money Total,
    Money MinimumTotal)
{
    public static TariffEvaluationResult Failed(TariffEvaluationFailure failure) =>
        new(failure, null, new Money(0), new Money(0), new Money(0), new Money(0), new Money(0));
}

public sealed class TariffRuleEvaluator
{
    public TariffEvaluationResult Evaluate(TariffEvaluationContext context, IEnumerable<TariffRule> availableRules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(availableRules);

        if (context.OwnerOrganizationId == Guid.Empty || context.CityId == Guid.Empty)
        {
            throw new ArgumentException("Pricing context identifiers are required.");
        }

        if (RequiresConsolidatedRoute(context.PricingTier) && !context.ConsolidatedRoute)
        {
            return TariffEvaluationResult.Failed(TariffEvaluationFailure.ConsolidatedRouteRequired);
        }

        var applicable = availableRules.Where(rule =>
                rule.OwnerOrganizationId == context.OwnerOrganizationId &&
                rule.CityId == context.CityId &&
                rule.PricingTier == context.PricingTier &&
                rule.ServiceType == context.ServiceType &&
                rule.Status == TariffRuleStatus.Active &&
                rule.ActiveFrom <= context.EvaluatedAt &&
                (rule.ActiveTo is null || rule.ActiveTo > context.EvaluatedAt))
            .Where(rule => IsSpatiallyApplicable(rule, context))
            .ToArray();

        TariffRule[] selected;
        if (context.PrivateTariffId is { } privateId)
        {
            selected = applicable.Where(rule => rule.Id == privateId).ToArray();
        }
        else
        {
            var bestSpecificity = applicable.Select(Specificity).DefaultIfEmpty(-1).Max();
            selected = applicable.Where(rule => Specificity(rule) == bestSpecificity).ToArray();
        }

        if (selected.Length == 0)
        {
            return TariffEvaluationResult.Failed(TariffEvaluationFailure.NoRule);
        }

        if (selected.Length > 1)
        {
            return TariffEvaluationResult.Failed(TariffEvaluationFailure.AmbiguousRule);
        }

        var rule = selected[0];
        // PRC-POLICY-VERSION-PER-ORG: a quote freezes the policy version of the rule it selected, so
        // a rule without one (only possible for a rule that predates the decision) is never quoted.
        // The check runs after selection: an unversioned rule is not skipped in favor of a less
        // specific one, which would silently change the price.
        if (!PricingPolicyVersionFormat.IsValid(rule.PolicyVersion))
        {
            return TariffEvaluationResult.Failed(TariffEvaluationFailure.PolicyVersionMissing);
        }

        // GATE-011-VAT-INCLUDED-2026-09-29: prices are presented with IVA included for every organization.
        // A rule in any other tax mode fails closed after selection (it is not skipped in favor of a less
        // specific rule, which would silently change the price).
        if (!PilotTaxPolicy.IsSelectable(rule.TaxMode))
        {
            return TariffEvaluationResult.Failed(TariffEvaluationFailure.TaxModeBlocked);
        }

        var amounts = TariffTaxCalculator.Calculate(rule.TaxMode, rule.AmountCents);
        if (Money.Add(Money.Subtract(amounts.Subtotal, amounts.Discount), amounts.Tax) != amounts.Total)
        {
            throw new InvalidOperationException("The taxed amounts are inconsistent.");
        }

        // The frozen floor (ADR-021) is the total the customer pays for the selected rule: with IVA included
        // it is the tariff amount itself, never the pre-tax subtotal.
        return new TariffEvaluationResult(
            TariffEvaluationFailure.None,
            rule,
            amounts.Subtotal,
            amounts.Discount,
            amounts.Tax,
            amounts.Total,
            amounts.Total);
    }

    public static bool RequiresConsolidatedRoute(PricingTier tier) =>
        tier is PricingTier.Business200To499 or PricingTier.Business500Plus;

    private static bool IsSpatiallyApplicable(TariffRule rule, TariffEvaluationContext context)
    {
        if (rule.OperatingZoneId is { } zoneId)
        {
            return context.SharedOperatingZoneId == zoneId;
        }

        if (rule.ServiceAreaId is { } areaId)
        {
            return context.SharedServiceAreaId == areaId;
        }

        return true;
    }

    private static int Specificity(TariffRule rule) =>
        rule.OperatingZoneId is not null ? 2 : rule.ServiceAreaId is not null ? 1 : 0;
}
