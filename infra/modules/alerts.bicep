// Application Insights is workspace based. Functions host request telemetry
// supplies invocation success/name; worker HTTP spans alone cannot prove runs.
param location string
param workspaceResourceId string
param apiAppName string
param functionsAppName string
param actionGroupResourceId string
param enabled bool = false
param tags object

var actions = empty(actionGroupResourceId) ? [] : [actionGroupResourceId]
var roles = '| where AppRoleName in (\'${apiAppName}\', \'${functionsAppName}\', \'SpeakerPipeline.Api\', \'SpeakerPipeline.Hosting.Functions\')'

resource failures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'speaker-pipeline-production-failures'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: 'Speaker pipeline: API or agent execution failure'
    description: 'API 5xx or failed Functions invocation in the last five minutes.'
    enabled: enabled && !empty(actionGroupResourceId)
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [workspaceResourceId]
    criteria: {
      allOf: [{
        query: format('''
AppRequests
{0}
| where toint(ResultCode) >= 500 or (Success == false and (Name contains 'Trigger' or Name contains 'TelegramWebhook'))
''', roles)
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: 0
        failingPeriods: {
          numberOfEvaluationPeriods: 1
          minFailingPeriodsToAlert: 1
        }
      }]
    }
    autoMitigate: true
    actions: {
      actionGroups: actions
    }
  }
}

// Two hours of grace after each UTC schedule accommodates processing/ingestion.
// Daily queries look back at most two days. Weekly scoring evaluates on Monday;
// its alert remains active until a success, or the next day's evaluation.
var schedules = [
  {
    name: 'DiscoveryTimerTrigger'
    dueMinutes: 300
    weekly: false
  }
  {
    name: 'TrackerMaintenanceTimerTrigger'
    dueMinutes: 330
    weekly: false
  }
  {
    name: 'ScoringAgentTimerTrigger'
    dueMinutes: 360
    weekly: true
  }
]

resource missedRuns 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for schedule in schedules: {
  name: 'speaker-pipeline-production-missed-${schedule.name}'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: 'Speaker pipeline: missing ${schedule.name} success'
    description: 'Expected scheduled run has no successful Functions invocation after two hours. Disable during planned maintenance.'
    enabled: enabled && !empty(actionGroupResourceId)
    severity: 2
    evaluationFrequency: 'PT15M'
    windowSize: 'P2D'
    scopes: [workspaceResourceId]
    criteria: {
      allOf: [{
        query: format('''
let todayDue = startofday(now()) + {0}m;
let weekly = {1};
let expected = iff(now() < todayDue + 2h, todayDue - 1d, todayDue);
let shouldCheck = iff(weekly, dayofweek(now()) == 1d and now() >= todayDue + 2h, true);
AppRequests
| where AppRoleName in ('{2}', 'SpeakerPipeline.Hosting.Functions')
| where Name has '{3}' and Success == true
| where TimeGenerated >= iff(weekly, todayDue, expected)
| summarize SuccessfulRuns = count()
| where shouldCheck and SuccessfulRuns == 0
''', schedule.dueMinutes, string(schedule.weekly), functionsAppName, schedule.name)
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: 0
        failingPeriods: {
          numberOfEvaluationPeriods: 1
          minFailingPeriodsToAlert: 1
        }
      }]
    }
    autoMitigate: true
    actions: {
      actionGroups: actions
    }
  }
}]
