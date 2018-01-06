using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OrchardCore.Entities;
using OrchardCore.Scripting;
using OrchardCore.Workflows.Helpers;
using OrchardCore.Workflows.Models;

namespace OrchardCore.Workflows.Services
{
    public class WorkflowManager : IWorkflowManager
    {
        private readonly IActivityLibrary _activityLibrary;
        private readonly IWorkflowDefinitionRepository _workflowDefinitionRepository;
        private readonly IWorkflowInstanceRepository _workInstanceRepository;
        private readonly IScriptingManager _scriptingManager;
        private readonly IEnumerable<IWorkflowContextProvider> _workflowContextProviders;
        private readonly ILogger _logger;

        public WorkflowManager
        (
            IActivityLibrary activityLibrary,
            IWorkflowDefinitionRepository workflowDefinitionRepository,
            IWorkflowInstanceRepository workflowInstanceRepository,
            IScriptingManager scriptingManager,
            IEnumerable<IWorkflowContextProvider> workflowContextProviders,
            ILogger<WorkflowManager> logger
        )
        {
            _activityLibrary = activityLibrary;
            _workflowDefinitionRepository = workflowDefinitionRepository;
            _workInstanceRepository = workflowInstanceRepository;
            _scriptingManager = scriptingManager;
            _workflowContextProviders = workflowContextProviders;
            _logger = logger;
        }

        public WorkflowContext CreateWorkflowContext(WorkflowDefinitionRecord workflowDefinitionRecord, WorkflowInstanceRecord workflowInstanceRecord)
        {
            var activityQuery = workflowDefinitionRecord.Activities.Select(CreateActivityContext);
            var context = new WorkflowContext(workflowDefinitionRecord, workflowInstanceRecord, activityQuery, _scriptingManager);

            foreach (var provider in _workflowContextProviders)
            {
                provider.Configure(context);
            }

            return context;
        }

        public ActivityContext CreateActivityContext(ActivityRecord activityRecord)
        {
            var activity = _activityLibrary.InstantiateActivity(activityRecord.Name);
            var entity = activity as Entity;

            entity.Properties = activityRecord.Properties;
            return new ActivityContext
            {
                ActivityRecord = activityRecord,
                Activity = activity
            };
        }

        public async Task TriggerEventAsync(string name, IDictionary<string, object> input = null, string correlationId = null)
        {
            var activity = _activityLibrary.GetActivityByName(name);

            if (activity == null)
            {
                _logger.LogError("Activity {0} was not found", name);
                return;
            }

            // Look for workflow definitions with a corresponding starting activity.
            var workflowsToStart = await _workflowDefinitionRepository.GetWorkflowDefinitionsByStartActivityAsync(name);

            // And any running workflow paused on this kind of activity for the specified target.
            // When an activity is restarted, all the other ones of the same workflow are cancelled.
            var awaitingWorkflowInstances = await _workInstanceRepository.GetWaitingWorkflowInstancesAsync(name, correlationId);

            // If no activity record is matching the event, do nothing.
            if (!workflowsToStart.Any() && !awaitingWorkflowInstances.Any())
            {
                return;
            }

            // Resume pending workflows.
            foreach (var workflowInstance in awaitingWorkflowInstances)
            {
                // Merge additional input, if any.
                if (input?.Any() == true)
                {
                    var workflowState = workflowInstance.State.ToObject<WorkflowState>();
                    workflowState.Input.Merge(input);
                    workflowInstance.State = JObject.FromObject(workflowState);
                }

                await ResumeWorkflowAsync(workflowInstance);
            }

            // Start new workflows.
            foreach (var workflowToStart in workflowsToStart)
            {
                var startActivity = workflowToStart.Activities.FirstOrDefault(x => x.IsStart && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

                if (startActivity != null)
                {
                    await StartWorkflowAsync(workflowToStart, startActivity, input);
                }
            }
        }

        public async Task ResumeWorkflowAsync(WorkflowInstanceRecord workflowInstance)
        {
            foreach (var awaitingActivity in workflowInstance.AwaitingActivities.ToList())
            {
                await ResumeWorkflowAsync(workflowInstance, awaitingActivity);
            }
        }

        public async Task ResumeWorkflowAsync(WorkflowInstanceRecord workflowInstance, AwaitingActivityRecord awaitingActivity)
        {
            var workflowDefinition = await _workflowDefinitionRepository.GetWorkflowDefinitionAsync(workflowInstance.DefinitionId);
            var activityRecord = workflowDefinition.Activities.SingleOrDefault(x => x.Id == awaitingActivity.ActivityId);
            var workflowContext = CreateWorkflowContext(workflowDefinition, workflowInstance);

            // Signal every activity that the workflow is about to be resumed.
            var cancellationToken = new CancellationToken();
            InvokeActivities(workflowContext, x => x.Activity.OnWorkflowResuming(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                return;
            }

            // Signal every activity that the workflow is resumed.
            InvokeActivities(workflowContext, x => x.Activity.OnWorkflowResumed(workflowContext));

            // Remove the awaiting activity.
            workflowContext.WorkflowInstance.AwaitingActivities.Remove(awaitingActivity);

            // Resume the workflow at the specified blocking activity.
            var blockedOn = (await ExecuteWorkflowAsync(workflowContext, activityRecord)).ToList();

            // Check if the workflow halted on any blocking activities, and if there are no more awaiting activities.
            if (blockedOn.Count == 0 && workflowContext.WorkflowInstance.AwaitingActivities.Count == 0)
            {
                // No, delete the workflow.
                _workInstanceRepository.Delete(workflowContext.WorkflowInstance);
            }
            else
            {
                // Add the new ones.
                foreach (var blocking in blockedOn)
                {
                    workflowContext.WorkflowInstance.AwaitingActivities.Add(AwaitingActivityRecord.FromActivity(blocking));
                }

                // Serialize state.
                _workInstanceRepository.Save(workflowContext);
            }
        }

        public async Task<WorkflowContext> StartWorkflowAsync(WorkflowDefinitionRecord workflowDefinition, ActivityRecord startActivity = null, IDictionary<string, object> input = null, string correlationId = null)
        {
            if (startActivity == null)
            {
                startActivity = workflowDefinition.Activities.FirstOrDefault(x => x.IsStart);

                if (startActivity == null)
                {
                    throw new InvalidOperationException($"Workflow with ID {workflowDefinition.Id} does not have a start activity.");
                }
            }

            // Create a new workflow instance.
            var workflowInstance = new WorkflowInstanceRecord
            {
                DefinitionId = workflowDefinition.Id,
                State = JObject.FromObject(new WorkflowState { Input = input ?? new Dictionary<string, object>() }),
                CorrelationId = correlationId
            };

            // Create a workflow context.
            var workflowContext = CreateWorkflowContext(workflowDefinition, workflowInstance);

            // Signal every activity that the workflow is about to start.
            var cancellationToken = new CancellationToken();
            InvokeActivities(workflowContext, x => x.Activity.OnWorkflowStarting(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                return workflowContext;
            }

            // Signal every activity that the workflow has started.
            InvokeActivities(workflowContext, x => x.Activity.OnWorkflowStarted(workflowContext));

            // Execute the activity.
            var blockedOn = (await ExecuteWorkflowAsync(workflowContext, startActivity)).ToList();

            // Is the workflow halted on a blocking activity?
            if (blockedOn.Count == 0)
            {
                // No, nothing to do.
            }
            else
            {
                // Workflow halted, create a workflow state.
                foreach (var blocking in blockedOn)
                {
                    workflowContext.WorkflowInstance.AwaitingActivities.Add(AwaitingActivityRecord.FromActivity(blocking));
                }

                // Serialize state.
                _workInstanceRepository.Save(workflowContext);
            }

            return workflowContext;
        }

        public async Task<IEnumerable<ActivityRecord>> ExecuteWorkflowAsync(WorkflowContext workflowContext, ActivityRecord activity)
        {
            var definition = await _workflowDefinitionRepository.GetWorkflowDefinitionAsync(workflowContext.WorkflowDefinition.Id);
            var firstPass = true;
            var scheduled = new Stack<ActivityRecord>();

            scheduled.Push(activity);

            var blocking = new List<ActivityRecord>();

            while (scheduled.Count > 0)
            {
                activity = scheduled.Pop();

                var activityContext = workflowContext.GetActivity(activity.Id);

                // Check if the current activity can execute.
                if (!await activityContext.Activity.CanExecuteAsync(workflowContext, activityContext))
                {
                    // No, so break out and return.
                    break;
                }

                // While there is an activity to process.
                if (!firstPass)
                {
                    if (activityContext.Activity.IsEvent())
                    {
                        blocking.Add(activity);
                        continue;
                    }
                }
                else
                {
                    firstPass = false;
                }

                // Signal every activity that the activity is about to be executed.
                var cancellationToken = new CancellationToken();
                InvokeActivities(workflowContext, x => x.Activity.OnActivityExecuting(workflowContext, activityContext, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                {
                    // Activity is aborted.
                    continue;
                }

                // Execute the current activity.
                var outcomes = (await activityContext.Activity.ExecuteAsync(workflowContext, activityContext)).ToList();

                // Signal every activity that the activity is executed.
                InvokeActivities(workflowContext, x => x.Activity.OnActivityExecuted(workflowContext, activityContext));

                foreach (var outcome in outcomes)
                {
                    // Look for next activity in the graph.
                    var transition = definition.Transitions.FirstOrDefault(x => x.SourceActivityId == activity.Id && x.SourceOutcomeName == outcome);

                    if (transition != null)
                    {
                        var destinationActivity = workflowContext.WorkflowDefinition.Activities.SingleOrDefault(x => x.Id == transition.DestinationActivityId);
                        scheduled.Push(destinationActivity);
                    }
                }
            }

            // Apply Distinct() as two paths could block on the same activity.
            return blocking.Distinct();
        }

        /// <summary>
        /// Executes a specific action on all the activities of a workflow.
        /// </summary>
        private void InvokeActivities(WorkflowContext workflowContext, Action<ActivityContext> action)
        {
            foreach (var activity in workflowContext.Activities)
            {
                action(activity);
            }
        }
    }
}