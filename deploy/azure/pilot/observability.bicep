// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 5 of 5: OBS-002 alerts, action group and workbook.
// Everything here reads the Container Apps console and system logs that platform.bicep already sends
// to Log Analytics. The queries use only the OBS-002 structured events (fixed lane, job and outcome
// names, status classes and counts; see docs/development/obs-002-pilot-observability.md), so no alert,
// notification or workbook ever shows a payload, token, tenant or personal datum.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('E-mail that receives the OBS-002 alerts. No default: the owner supplies it in observability.parameters.json.')
@minLength(6)
param alertEmailAddress string

@description('Outbox lag: oldest claimed message waited longer than this many seconds after it became available.')
@minValue(60)
@maxValue(3600)
param outboxLagThresholdSeconds int = 300

@description('API 5xx: minimum 5xx responses in the window before the rate is considered.')
@minValue(1)
param http5xxMinimumCount int = 5

@description('API 5xx: alert when 5xx responses are at least this percentage of all responses in the window.')
@minValue(1)
@maxValue(100)
param http5xxPercentThreshold int = 5

@description('Readiness: probe failures or restart events per app in the window before alerting.')
@minValue(1)
param readinessEventThreshold int = 3

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'PILOT'
  contract: 'ENV-001'
  dataClassification: 'REAL_PEOPLE'
  cleanupGroup: 'ENV-001'
}

// Log search alerts are billed per rule by evaluation frequency (Azure Retail Prices API, mexicocentral,
// 2026-09-28): PT15M 0.55, PT10M 1.10, PT5M 1.65 USD per month. Five rules, all at PT15M = 2.75 USD per
// month (OBS-002-ALERTS-15MIN-COST-2026-10-02: readiness also every 15 minutes, window 15 minutes). The workbook is free and the first 1,000 alert e-mails per month are free.

// Event ids in the queries: 4601 OutboxLaneSummary, 4602 ScheduledJobCycle, 4603 HttpStatusSummary
// (Paqueteria.Infrastructure.Observability.TelemetryEvents) and 4004 OutboxRetentionLaneCompleted (OPS-004).
// Apps: ca-pv-pilot-api, ca-pv-pilot-worker, ca-pv-pilot-web (apps.bicep).

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-02-01' existing = {
  name: 'law-pv-pilot-${suffix}'
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-pv-pilot-ops'
  location: 'Global'
  tags: tags
  properties: {
    groupShortName: 'pvpilotops'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmailAddress
        useCommonAlertSchema: true
      }
    ]
  }
}

// ---------------------------------------------------------------- queries
var laneSummaries = '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s in ("ca-pv-pilot-api", "ca-pv-pilot-worker")
| where Log_s has "4601"
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 4601
| extend Lane = tostring(e.State.Lane), Claimed = tolong(e.State.Claimed), Dead = tolong(e.State.Dead),
    LoopFailures = tolong(e.State.LoopFailures), MaxClaimAgeMs = tolong(e.State.MaxClaimAgeMs)
'''

// Lag or stall: a lane whose oldest claimed message waited too long, whose loop keeps failing, or
// that wrote no summary at all in the window (the dispatcher loop stopped or the host is down).
var outboxLagQuery = 'let lagMs = ${outboxLagThresholdSeconds * 1000};\nlet summaries = ${laneSummaries}| summarize Reports = count(), MaxClaimAgeMs = max(MaxClaimAgeMs), LoopFailures = sum(LoopFailures) by Lane;\n${outboxLagTail}'
var outboxLagTail = '''
datatable(Lane: string)["notifications", "dispatch", "realtime_business", "realtime_location"]
| join kind=leftouter summaries on Lane
| extend Reports = coalesce(Reports, long(0)), MaxClaimAgeMs = coalesce(MaxClaimAgeMs, long(0)), LoopFailures = coalesce(LoopFailures, long(0))
| where Reports == 0 or MaxClaimAgeMs > lagMs or LoopFailures >= 10
| project Lane, Reports, MaxClaimAgeMs, LoopFailures
'''

var deadGrowthQuery = '${laneSummaries}| summarize Dead = sum(Dead) by Lane\n| where Dead > 0\n'

// Retention (OPS-004 lane result 4004) or any scheduled job cycle (4602) failed, or retention wrote no
// successful lane result for an hour (it runs every 15 minutes over two lanes).
var jobFailureQuery = '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-pv-pilot-worker"
| where Log_s has "4004" or Log_s has "4602"
| extend e = parse_json(Log_s)
| extend EventId = toint(e.EventId), Outcome = tostring(e.State.Outcome), Job = tostring(e.State.Job), Lane = tostring(e.State.Lane)
| where EventId in (4004, 4602)
| summarize Failures = countif(Outcome == "failure"), RetentionSuccesses = countif(EventId == 4004 and Outcome == "success"),
    FailedJobs = make_set_if(Job, EventId == 4602 and Outcome == "failure", 10), FailedRetentionLanes = make_set_if(Lane, EventId == 4004 and Outcome == "failure", 4)
| where Failures > 0 or RetentionSuccesses == 0
'''

// Readiness degradation: probe failures, crash loops and restarts reported by the platform.
var readinessQuery = 'let threshold = ${readinessEventThreshold};\n${readinessTail}'
var readinessTail = '''
ContainerAppSystemLogs_CL
| where ContainerAppName_s in ("ca-pv-pilot-api", "ca-pv-pilot-worker", "ca-pv-pilot-web")
| where Reason_s in~ ("ProbeFailed", "ContainerCrashing", "ContainerBackOff", "BackOff", "ContainerTerminated", "ReplicaUnhealthy")
    or (Type_s =~ "Warning" and Log_s has "probe")
| summarize Events = count() by ContainerAppName_s, Reason_s
| where Events >= threshold
'''

var http5xxQuery = 'let minimum5xx = ${http5xxMinimumCount};\nlet percent = ${http5xxPercentThreshold};\n${http5xxTail}'
var http5xxTail = '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-pv-pilot-api"
| where Log_s has "4603"
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 4603
| summarize Total = sum(tolong(e.State.Total)), Status5xx = sum(tolong(e.State.Status5xx))
| where Status5xx >= minimum5xx and Status5xx * 100.0 / Total >= percent
'''

var rules = [
  {
    name: 'sqr-pv-pilot-outbox-lag'
    displayName: 'PILOT outbox lag or stalled lane'
    description: 'OBS-002: an outbox lane claimed a message that waited too long, keeps failing its loop, or reported nothing for 15 minutes.'
    severity: 2
    frequency: 'PT15M'
    window: 'PT15M'
    query: outboxLagQuery
  }
  {
    name: 'sqr-pv-pilot-outbox-dead'
    displayName: 'PILOT outbox DEAD growth'
    description: 'OBS-002: at least one outbox message was settled DEAD in the last 15 minutes.'
    severity: 2
    frequency: 'PT15M'
    window: 'PT15M'
    query: deadGrowthQuery
  }
  {
    name: 'sqr-pv-pilot-job-failure'
    displayName: 'PILOT retention or scheduled job failure'
    description: 'OBS-002: OPS-004 retention or a scheduled Worker job failed, or retention reported no success for an hour.'
    severity: 2
    frequency: 'PT15M'
    window: 'PT1H'
    query: jobFailureQuery
  }
  {
    name: 'sqr-pv-pilot-readiness'
    displayName: 'PILOT readiness degradation'
    description: 'OBS-002: repeated probe failures, crash loops or restarts of the API, Worker or Web in the last 15 minutes.'
    severity: 1
    frequency: 'PT15M'
    window: 'PT15M'
    query: readinessQuery
  }
  {
    name: 'sqr-pv-pilot-api-5xx'
    displayName: 'PILOT API 5xx rate'
    description: 'OBS-002: the API answered too many 5xx responses in the last 15 minutes.'
    severity: 2
    frequency: 'PT15M'
    window: 'PT15M'
    query: http5xxQuery
  }
]

resource alertRules 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for rule in rules: {
  name: rule.name
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: rule.displayName
    description: rule.description
    severity: rule.severity
    enabled: true
    evaluationFrequency: rule.frequency
    windowSize: rule.window
    scopes: [
      logAnalytics.id
    ]
    // The _CL tables appear only after the first container logs; the queries are checked statically.
    skipQueryValidation: true
    // Stateful: one notification when a condition starts and one when it resolves.
    autoMitigate: true
    criteria: {
      allOf: [
        {
          query: rule.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}]

// ---------------------------------------------------------------- workbook (free)
var workbookQueries = {
  httpByStatusClass: '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-pv-pilot-api" and Log_s has "4603"
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 4603
| summarize ["2xx"] = sum(tolong(e.State.Status2xx)), ["3xx"] = sum(tolong(e.State.Status3xx)), ["4xx"] = sum(tolong(e.State.Status4xx)), ["5xx"] = sum(tolong(e.State.Status5xx)) by bin(TimeGenerated, 15m)
| order by TimeGenerated asc
'''
  jobOutcomes: '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "ca-pv-pilot-worker" and (Log_s has "4602" or Log_s has "4004")
| extend e = parse_json(Log_s)
| extend EventId = toint(e.EventId)
| where EventId in (4602, 4004)
| extend Job = iff(EventId == 4004, strcat("outbox.retention/", tostring(e.State.Lane)), tostring(e.State.Job)), Outcome = tostring(e.State.Outcome)
| summarize Cycles = count(), LastSeen = max(TimeGenerated) by Job, Outcome
| order by Job asc, Outcome asc
'''
  laneSummaries: '''
ContainerAppConsoleLogs_CL
| where ContainerAppName_s in ("ca-pv-pilot-api", "ca-pv-pilot-worker") and Log_s has "4601"
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 4601
| summarize Claimed = sum(tolong(e.State.Claimed)), Processed = sum(tolong(e.State.Processed)), Retry = sum(tolong(e.State.Retry)),
    Dead = sum(tolong(e.State.Dead)), LoopFailures = sum(tolong(e.State.LoopFailures)), MaxClaimAgeMs = max(tolong(e.State.MaxClaimAgeMs)),
    LastSummary = max(TimeGenerated) by Lane = tostring(e.State.Lane)
| order by Lane asc
'''
  platformEvents: '''
ContainerAppSystemLogs_CL
| where ContainerAppName_s in ("ca-pv-pilot-api", "ca-pv-pilot-worker", "ca-pv-pilot-web")
| where Type_s =~ "Warning" or Reason_s in~ ("ProbeFailed", "ContainerCrashing", "ContainerBackOff", "BackOff", "ContainerTerminated", "ReplicaUnhealthy")
| summarize Events = count() by bin(TimeGenerated, 1h), ContainerAppName_s, Reason_s
| order by TimeGenerated desc
'''
}

var workbookItems = [
  {
    type: 1
    name: 'intro'
    content: {
      json: '## Paquetenvia pilot (OBS-002)\nCounts and fixed names only: no payloads, tokens, tenants or personal data.'
    }
  }
  {
    type: 3
    name: 'http-status-classes'
    content: {
      version: 'KqlItem/1.0'
      title: 'API responses by status class (15 min)'
      query: workbookQueries.httpByStatusClass
      queryType: 0
      resourceType: 'microsoft.operationalinsights/workspaces'
      visualization: 'timechart'
      timeContext: {
        durationMs: 86400000
      }
    }
  }
  {
    type: 3
    name: 'worker-job-outcomes'
    content: {
      version: 'KqlItem/1.0'
      title: 'Worker job outcomes (24 h)'
      query: workbookQueries.jobOutcomes
      queryType: 0
      resourceType: 'microsoft.operationalinsights/workspaces'
      visualization: 'table'
      timeContext: {
        durationMs: 86400000
      }
    }
  }
  {
    type: 3
    name: 'outbox-lanes'
    content: {
      version: 'KqlItem/1.0'
      title: 'Outbox lanes (24 h)'
      query: workbookQueries.laneSummaries
      queryType: 0
      resourceType: 'microsoft.operationalinsights/workspaces'
      visualization: 'table'
      timeContext: {
        durationMs: 86400000
      }
    }
  }
  {
    type: 3
    name: 'platform-events'
    content: {
      version: 'KqlItem/1.0'
      title: 'Probe failures and restarts (7 d)'
      query: workbookQueries.platformEvents
      queryType: 0
      resourceType: 'microsoft.operationalinsights/workspaces'
      visualization: 'table'
      timeContext: {
        durationMs: 604800000
      }
    }
  }
]

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'obs-002-pilot-workbook')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Paquetenvia pilot operations (OBS-002)'
    category: 'workbook'
    sourceId: logAnalytics.id
    serializedData: string({
      version: 'Notebook/1.0'
      items: workbookItems
      isLocked: false
      fallbackResourceIds: [
        logAnalytics.id
      ]
    })
  }
}

output actionGroupName string = actionGroup.name
output alertRuleNames array = [for (rule, i) in rules: alertRules[i].name]
output workbookName string = workbook.name
