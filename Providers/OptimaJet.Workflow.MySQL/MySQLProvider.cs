using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using OptimaJet.Workflow.Core;
using OptimaJet.Workflow.Core.Fault;
using OptimaJet.Workflow.Core.Model;
using OptimaJet.Workflow.Core.Persistence;
using OptimaJet.Workflow.Core.Runtime;
using OptimaJet.Workflow.Core.Runtime.Timers;

namespace OptimaJet.Workflow.MySQL
{
    public class MySQLProvider : IWorkflowProvider, IApprovalProvider
    {
        public string ConnectionString { get; set; }
        private WorkflowRuntime _runtime;
        private readonly bool WriteToHistory;
        private readonly bool WriteSubProcessToRoot;

        public void Init(WorkflowRuntime runtime)
        {
            _runtime = runtime;
        }

        public MySQLProvider(string connectionString, bool writeToHistory = true, bool writeSubProcessToRoot = false)
        {
            ConnectionString = connectionString;
            WriteToHistory = writeToHistory;
            WriteSubProcessToRoot = writeSubProcessToRoot;
        }

        #region IPersistenceProvider

        public void DeleteInactiveTimersByProcessId(Guid processId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.DeleteInactiveByProcessId(connection, processId);
            }
        }

        public Task DeleteTimerAsync(Guid timerId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.Delete(connection, timerId);
                return Task.FromResult(true);
            }
        }

        public List<Guid> GetRunningProcesses(string runtimeId = null)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowProcessInstanceStatus.GetProcessesByStatus(connection, ProcessStatus.Running.Id, runtimeId);
            }
        }

        public WorkflowRuntimeModel CreateWorkflowRuntime(string runtimeId, RuntimeStatus status)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var runtime = new Models.WorkflowRuntime() {RuntimeId = runtimeId, Lock = Guid.NewGuid(), Status = status};

                runtime.Insert(connection);

                return new WorkflowRuntimeModel {Lock = runtime.Lock, RuntimeId = runtimeId, Status = status};
            }
        }

        public void DeleteWorkflowRuntime(string name)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                Models.WorkflowRuntime.Delete(connection, name);
            }
        }

        public WorkflowRuntimeModel UpdateWorkflowRuntimeStatus(WorkflowRuntimeModel runtime, RuntimeStatus status)
        {
            var res = UpdateWorkflowRuntime(runtime, x => x.Status = status, Models.WorkflowRuntime.UpdateStatus);

            if (res.Item1 != 1)
            {
                throw new ImpossibleToSetRuntimeStatusException();
            }

            return res.Item2;
        }

        public (bool Success, WorkflowRuntimeModel UpdatedModel) UpdateWorkflowRuntimeRestorer(WorkflowRuntimeModel runtime, string restorerId)
        {
            var res = UpdateWorkflowRuntime(runtime, x => x.RestorerId = restorerId, Models.WorkflowRuntime.UpdateRestorer);

            return (res.Item1 == 1, res.Item2);
        }

        public bool MultiServerRuntimesExist()
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.MultiServerRuntimesExist(connection);
            }
        }

        public int SingleServerRuntimesCount()
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.SingleServerRuntimesCount(connection);
            }
        }

        public int ActiveMultiServerRuntimesCount(string currentRuntimeId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.ActiveMultiServerRuntimesCount(connection, currentRuntimeId);
            }
        }

        public void InitializeProcess(ProcessInstance processInstance)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var oldProcess = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);
                if (oldProcess != null)
                {
                    throw new ProcessAlreadyExistsException(processInstance.ProcessId);
                }

                var newProcess = new WorkflowProcessInstance
                {
                    Id = processInstance.ProcessId,
                    SchemeId = processInstance.SchemeId,
                    ActivityName = processInstance.ProcessScheme.InitialActivity.Name,
                    StateName = processInstance.ProcessScheme.InitialActivity.State,
                    RootProcessId = processInstance.RootProcessId,
                    ParentProcessId = processInstance.ParentProcessId,
                    TenantId = processInstance.TenantId,
                    StartingTransition = processInstance.ProcessScheme.StartingTransition
                };
                newProcess.Insert(connection);
            }
        }

        public void BindProcessToNewScheme(ProcessInstance processInstance)
        {
            BindProcessToNewScheme(processInstance, false);
        }

        public void BindProcessToNewScheme(ProcessInstance processInstance, bool resetIsDeterminingParametersChanged)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var oldProcess = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);
                if (oldProcess == null)
                    throw new ProcessNotFoundException(processInstance.ProcessId);

                oldProcess.SchemeId = processInstance.SchemeId;
                oldProcess.StartingTransition = processInstance.ProcessScheme.StartingTransition;
                if (resetIsDeterminingParametersChanged)
                    oldProcess.IsDeterminingParametersChanged = false;
                oldProcess.Update(connection);
            }
        }

        public void FillProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void FillPersistedProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetPersistedProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void FillSystemProcessParameters(ProcessInstance processInstance)
        {
            processInstance.AddParameters(GetSystemProcessParameters(processInstance.ProcessId, processInstance.ProcessScheme));
        }

        public void SavePersistenceParameters(ProcessInstance processInstance)
        {
            var parametersToPersistList =
                processInstance.ProcessParameters.Where(ptp => ptp.Purpose == ParameterPurpose.Persistence)
                    .Select(ptp =>
                    {
                        if (ptp.Type == typeof(UnknownParameterType))
                            return new {Parameter = ptp, SerializedValue = (string)ptp.Value};
                        return new {Parameter = ptp, SerializedValue = ParametersSerializer.Serialize(ptp.Value, ptp.Type)};
                    })
                    .ToList();

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var persistedParameters = WorkflowProcessInstancePersistence.SelectByProcessId(connection, processInstance.ProcessId).ToList();

                foreach (var parameterDefinitionWithValue in parametersToPersistList)
                {
                    var persistence =
                        persistedParameters.SingleOrDefault(
                            pp => pp.ParameterName == parameterDefinitionWithValue.Parameter.Name);
                    {
                        if (persistence == null)
                        {
                            if (parameterDefinitionWithValue.SerializedValue != null)
                            {
                                persistence = new WorkflowProcessInstancePersistence
                                {
                                    Id = Guid.NewGuid(),
                                    ProcessId = processInstance.ProcessId,
                                    ParameterName = parameterDefinitionWithValue.Parameter.Name,
                                    Value = parameterDefinitionWithValue.SerializedValue
                                };
                                persistence.Insert(connection);
                            }
                        }
                        else
                        {
                            if (parameterDefinitionWithValue.SerializedValue != null)
                            {
                                persistence.Value = parameterDefinitionWithValue.SerializedValue;
                                persistence.Update(connection);
                            }
                            else
                                WorkflowProcessInstancePersistence.Delete(connection, persistence.Id);
                        }
                    }
                }
            }
        }

        public void SetProcessStatus(Guid processId, ProcessStatus newStatus)
        {
            if (newStatus == ProcessStatus.Running)
            {
                SetRunningStatus(processId);
            }
            else
            {
                SetCustomStatus(processId, newStatus);
            }
        }

        public void SetWorkflowIniialized(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Initialized, true);
        }

        public void SetWorkflowIdled(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Idled);
        }

        public void SetWorkflowRunning(ProcessInstance processInstance)
        {
            var processId = processInstance.ProcessId;
            SetRunningStatus(processId);
        }

        public void SetWorkflowFinalized(ProcessInstance processInstance)
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Finalized);
        }

#pragma warning disable 612
        public void SetWorkflowTerminated(ProcessInstance processInstance)
#pragma warning restore 612
        {
            SetCustomStatus(processInstance.ProcessId, ProcessStatus.Terminated);
        }

        public void ResetWorkflowRunning()
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessInstanceStatus.MassChangeStatus(connection, ProcessStatus.Running.Id, ProcessStatus.Idled.Id, _runtime.RuntimeDateTimeNow);
            }
        }

        public void UpdatePersistenceState(ProcessInstance processInstance, TransitionDefinition transition)
        {
            var paramIdentityId = processInstance.GetParameter(DefaultDefinitions.ParameterIdentityId.Name);
            var paramImpIdentityId = processInstance.GetParameter(DefaultDefinitions.ParameterImpersonatedIdentityId.Name);

            var identityId = paramIdentityId == null ? string.Empty : (string)paramIdentityId.Value;
            var impIdentityId = paramImpIdentityId == null ? identityId : (string)paramImpIdentityId.Value;

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessInstance inst = WorkflowProcessInstance.SelectByKey(connection, processInstance.ProcessId);

                if (inst != null)
                {
                    if (!string.IsNullOrEmpty(transition.To.State))
                        inst.StateName = transition.To.State;

                    inst.ActivityName = transition.To.Name;
                    inst.PreviousActivity = transition.From.Name;

                    if (!string.IsNullOrEmpty(transition.From.State))
                        inst.PreviousState = transition.From.State;

                    if (transition.Classifier == TransitionClassifier.Direct)
                    {
                        inst.PreviousActivityForDirect = transition.From.Name;

                        if (!string.IsNullOrEmpty(transition.From.State))
                            inst.PreviousStateForDirect = transition.From.State;
                    }
                    else if (transition.Classifier == TransitionClassifier.Reverse)
                    {
                        inst.PreviousActivityForReverse = transition.From.Name;
                        if (!string.IsNullOrEmpty(transition.From.State))
                            inst.PreviousStateForReverse = transition.From.State;
                    }

                    inst.ParentProcessId = processInstance.ParentProcessId;
                    inst.RootProcessId = processInstance.RootProcessId;

                    inst.Update(connection);
                }

                if (!WriteToHistory)
                    return;

                var history = new WorkflowProcessTransitionHistory
                {
                    ActorIdentityId = impIdentityId,
                    ExecutorIdentityId = identityId,
                    Id = Guid.NewGuid(),
                    IsFinalised = transition.To.IsFinal,
                    ProcessId = (WriteSubProcessToRoot && processInstance.IsSubprocess) ? processInstance.RootProcessId : processInstance.ProcessId,
                    FromActivityName = transition.From.Name,
                    FromStateName = transition.From.State,
                    ToActivityName = transition.To.Name,
                    ToStateName = transition.To.State,
                    TransitionClassifier =
                        transition.Classifier.ToString(),
                    TransitionTime = _runtime.RuntimeDateTimeNow,
                    TriggerName = string.IsNullOrEmpty(processInstance.ExecutedTimer) ? processInstance.CurrentCommand : processInstance.ExecutedTimer
                };
                history.Insert(connection);
            }
        }

        public bool IsProcessExists(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowProcessInstance.SelectByKey(connection, processId) != null;
            }
        }

        public ProcessStatus GetInstanceStatus(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var instance = WorkflowProcessInstanceStatus.SelectByKey(connection, processId);
                if (instance == null)
                    return ProcessStatus.NotFound;
                var status = ProcessStatus.All.SingleOrDefault(ins => ins.Id == instance.Status);
                if (status == null)
                    return ProcessStatus.Unknown;
                return status;
            }
        }

        private void SetRunningStatus(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var instanceStatus = WorkflowProcessInstanceStatus.SelectByKey(connection, processId);
                if (instanceStatus == null)
                    throw new StatusNotDefinedException();

                if (instanceStatus.Status == ProcessStatus.Running.Id)
                    throw new ImpossibleToSetStatusException("Process already running");

                var oldLock = instanceStatus.Lock;

                instanceStatus.Lock = Guid.NewGuid();
                instanceStatus.Status = ProcessStatus.Running.Id;
                instanceStatus.RuntimeId = _runtime.Id;
                instanceStatus.SetTime = _runtime.RuntimeDateTimeNow;

                var cnt = WorkflowProcessInstanceStatus.ChangeStatus(connection, instanceStatus, oldLock);

                if (cnt != 1)
                    throw new ImpossibleToSetStatusException();
            }
        }

        private void SetCustomStatus(Guid processId, ProcessStatus status, bool createIfnotDefined = false)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var instanceStatus = WorkflowProcessInstanceStatus.SelectByKey(connection, processId);
                if (instanceStatus == null)
                {
                    if (!createIfnotDefined)
                        throw new StatusNotDefinedException();

                    instanceStatus = new WorkflowProcessInstanceStatus
                    {
                        Id = processId,
                        Lock = Guid.NewGuid(),
                        Status = status.Id,
                        RuntimeId = _runtime.Id,
                        SetTime = _runtime.RuntimeDateTimeNow
                    };
                    instanceStatus.Insert(connection);
                }
                else
                {
                    var oldLock = instanceStatus.Lock;

                    instanceStatus.Status = status.Id;
                    instanceStatus.Lock = Guid.NewGuid();
                    instanceStatus.RuntimeId = _runtime.Id;
                    instanceStatus.SetTime = _runtime.RuntimeDateTimeNow;

                    var cnt = WorkflowProcessInstanceStatus.ChangeStatus(connection, instanceStatus, oldLock);

                    if (cnt != 1)
                        throw new ImpossibleToSetStatusException();
                }

            }
        }

        private IEnumerable<ParameterDefinitionWithValue> GetProcessParameters(Guid processId, ProcessDefinition processDefinition)
        {
            var parameters = new List<ParameterDefinitionWithValue>(processDefinition.Parameters.Count());
            parameters.AddRange(GetPersistedProcessParameters(processId, processDefinition));
            parameters.AddRange(GetSystemProcessParameters(processId, processDefinition));
            return parameters;
        }

        private IEnumerable<ParameterDefinitionWithValue> GetSystemProcessParameters(Guid processId,
            ProcessDefinition processDefinition)
        {
            var processInstance = GetProcessInstance(processId);

            var systemParameters =
                processDefinition.Parameters.Where(p => p.Purpose == ParameterPurpose.System).ToList();

            var parameters = new List<ParameterDefinitionWithValue>(systemParameters.Count())
            {
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterProcessId.Name),
                    processId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousState.Name),
                    processInstance.PreviousState),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterCurrentState.Name),
                    processInstance.StateName),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousStateForDirect.Name),
                    processInstance.PreviousStateForDirect),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousStateForReverse.Name),
                    processInstance.PreviousStateForReverse),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivity.Name),
                    processInstance.PreviousActivity),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterCurrentActivity.Name),
                    processInstance.ActivityName),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivityForDirect.Name),
                    processInstance.PreviousActivityForDirect),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterPreviousActivityForReverse.Name),
                    processInstance.PreviousActivityForReverse),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterSchemeCode.Name),
                    processDefinition.Name),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterSchemeId.Name),
                    processInstance.SchemeId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterIsPreExecution.Name),
                    false),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterParentProcessId.Name),
                    processInstance.ParentProcessId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterRootProcessId.Name),
                    processInstance.RootProcessId),
                ParameterDefinition.Create(
                    systemParameters.Single(sp => sp.Name == DefaultDefinitions.ParameterTenantId.Name),
                    processInstance.TenantId)
            };
            return parameters;
        }

        private IEnumerable<ParameterDefinitionWithValue> GetPersistedProcessParameters(Guid processId, ProcessDefinition processDefinition)
        {
            var persistenceParameters = processDefinition.PersistenceParameters.ToList();
            var parameters = new List<ParameterDefinitionWithValue>(persistenceParameters.Count());

            List<WorkflowProcessInstancePersistence> persistedParameters;

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                persistedParameters = WorkflowProcessInstancePersistence.SelectByProcessId(connection, processId).ToList();
            }

            foreach (var persistedParameter in persistedParameters)
            {
                var parameterDefinition = persistenceParameters.FirstOrDefault(p => p.Name == persistedParameter.ParameterName);
                if (parameterDefinition == null)
                {
                    parameterDefinition =
                        ParameterDefinition.Create(persistedParameter.ParameterName, typeof(UnknownParameterType), ParameterPurpose.Persistence);
                    parameters.Add(ParameterDefinition.Create(parameterDefinition, persistedParameter.Value));
                }
                else
                {
                    parameters.Add(ParameterDefinition.Create(parameterDefinition,
                        ParametersSerializer.Deserialize(persistedParameter.Value, parameterDefinition.Type)));
                }
            }

            return parameters;
        }


        private WorkflowProcessInstance GetProcessInstance(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var processInstance = WorkflowProcessInstance.SelectByKey(connection, processId);
                if (processInstance == null)
                    throw new ProcessNotFoundException(processId);
                return processInstance;
            }
        }

        public void DeleteProcess(Guid[] processIds)
        {
            foreach (var processId in processIds)
                DeleteProcess(processId);
        }

        public void SaveGlobalParameter<T>(string type, string name, T value)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var parameter = WorkflowGlobalParameter.SelectByTypeAndName(connection, type, name).FirstOrDefault();

                if (parameter == null)
                {
                    parameter = new WorkflowGlobalParameter {Id = Guid.NewGuid(), Type = type, Name = name, Value = JsonConvert.SerializeObject(value)};

                    parameter.Insert(connection);
                }
                else
                {
                    parameter.Value = JsonConvert.SerializeObject(value);

                    parameter.Update(connection);
                }

            }
        }

        public T LoadGlobalParameter<T>(string type, string name)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var parameter = WorkflowGlobalParameter.SelectByTypeAndName(connection, type, name).FirstOrDefault();

                if (parameter == null)
                    return default(T);

                return JsonConvert.DeserializeObject<T>(parameter.Value);
            }

        }

        public List<T> LoadGlobalParameters<T>(string type)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var parameters = WorkflowGlobalParameter.SelectByTypeAndName(connection, type);

                return parameters.Select(p => JsonConvert.DeserializeObject<T>(p.Value)).ToList();
            }
        }

        public void DeleteGlobalParameters(string type, string name = null)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowGlobalParameter.DeleteByTypeAndName(connection, type, name);
            }
        }

        public void DeleteProcess(Guid processId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    WorkflowProcessInstance.Delete(connection, processId, transaction);
                    WorkflowProcessInstanceStatus.Delete(connection, processId, transaction);
                    WorkflowProcessInstancePersistence.DeleteByProcessId(connection, processId, transaction);
                    WorkflowProcessTransitionHistory.DeleteByProcessId(connection, processId, transaction);
                    WorkflowProcessTimer.DeleteByProcessId(connection, processId, null, transaction);
                    transaction.Commit();
                }
            }
        }

        public void RegisterTimer(Guid processId, Guid rootProcessId, string name, DateTime nextExecutionDateTime, bool notOverrideIfExists)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var timer = WorkflowProcessTimer.SelectByProcessIdAndName(connection, processId, name);
                if (timer == null)
                {
                    timer = new WorkflowProcessTimer
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        NextExecutionDateTime = nextExecutionDateTime,
                        ProcessId = processId,
                        RootProcessId = rootProcessId,
                        Ignore = false
                    };

                    timer.Insert(connection);
                }
                else if (!notOverrideIfExists)
                {
                    timer.NextExecutionDateTime = nextExecutionDateTime;
                    timer.Update(connection);
                }
            }
        }

        public void ClearTimers(Guid processId, List<string> timersIgnoreList)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.DeleteByProcessId(connection, processId, timersIgnoreList);
            }
        }

        public void ClearTimerIgnore(Guid timerId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.ClearTimerIgnore(connection, timerId);
            }
        }

        public int SetTimerIgnore(Guid timerId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowProcessTimer.SetTimerIgnore(connection, timerId);
            }
        }

        public void ClearTimer(Guid timerId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer.Delete(connection, timerId);
            }
        }

        public DateTime? GetCloseExecutionDateTime()
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var timer = WorkflowProcessTimer.GetCloseExecutionTimer(connection);
                if (timer == null)
                    return null;

                return timer.NextExecutionDateTime;
            }
        }

        public List<TimerToExecute> GetTimersToExecute()
        {
            var now = _runtime.RuntimeDateTimeNow;

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var timers = WorkflowProcessTimer.GetTimersToExecute(connection, now);
                WorkflowProcessTimer.SetIgnore(connection, timers);

                return timers.Select(t => new TimerToExecute {Name = t.Name, ProcessId = t.ProcessId, TimerId = t.Id}).ToList();
            }
        }

        public List<Core.Model.WorkflowTimer> GetTopTimersToExecute(int top)
        {
            DateTime now = _runtime.RuntimeDateTimeNow;

            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessTimer[] timers = WorkflowProcessTimer.GetTopTimersToExecute(connection, top, now);

                if (timers.Length == 0)
                {
                    return new List<Core.Model.WorkflowTimer>();
                }

                return timers.Select(t => new Core.Model.WorkflowTimer()
                {
                    Name = t.Name,
                    ProcessId = t.ProcessId,
                    TimerId = t.Id,
                    NextExecutionDateTime = t.NextExecutionDateTime,
                    RootProcessId = t.RootProcessId,
                }).ToList();
            }
        }

        public List<ProcessHistoryItem> GetProcessHistory(Guid processId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowProcessTransitionHistory.SelectByProcessId(connection, processId)
                    .Select(hi => new ProcessHistoryItem
                    {
                        ActorIdentityId = hi.ActorIdentityId,
                        ExecutorIdentityId = hi.ExecutorIdentityId,
                        FromActivityName = hi.FromActivityName,
                        FromStateName = hi.FromStateName,
                        IsFinalised = hi.IsFinalised,
                        ProcessId = hi.ProcessId,
                        ToActivityName = hi.ToActivityName,
                        ToStateName = hi.ToStateName,
                        TransitionClassifier = (TransitionClassifier)Enum.Parse(typeof(TransitionClassifier), hi.TransitionClassifier),
                        TransitionTime = hi.TransitionTime,
                        TriggerName = hi.TriggerName
                    })
                    .ToList();
            }
        }

        public IEnumerable<ProcessTimer> GetTimersForProcess(Guid processId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var timers = WorkflowProcessTimer.SelectByProcessId(connection, processId);
                return timers.Select(t => new ProcessTimer(t.Id, t.Name, t.NextExecutionDateTime));
            }
        }

        public async Task<List<IProcessInstanceTreeItem>> GetProcessInstanceTreeAsync(Guid rootProcessId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return await ProcessInstanceTreeItem.GetProcessTreeItemsByRootProcessId(connection, rootProcessId).ConfigureAwait(false);
            }
        }

        public IEnumerable<ProcessTimer> GetActiveTimersForProcess(Guid processId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var timers = WorkflowProcessTimer.SelectActiveByProcessId(connection, processId);
                return timers.Select(t => new ProcessTimer(t.Id, t.Name, t.NextExecutionDateTime));
            }
        }

        public WorkflowRuntimeModel GetWorkflowRuntimeModel(string runtimeId)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.GetWorkflowRuntimeStatus(connection, runtimeId);
            }
        }

        public DateTime? GetNextTimerDate(TimerCategory timerCategory, int timerInterval)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                var timerCategoryName = timerCategory.ToString();
                var syncLock = Models.WorkflowSync.GetByName(connection, timerCategoryName);

                if (syncLock == null)
                {
                    throw new Exception($"Sync lock {timerCategoryName} not found");
                }

                string nextTimeColumnName = null;

                switch (timerCategory)
                {
                    case TimerCategory.Timer:
                        nextTimeColumnName = "NextTimerTime";
                        break;
                    case TimerCategory.ServiceTimer:
                        nextTimeColumnName = "NextServiceTimerTime";
                        break;
                    default:
                        throw new Exception($"Unknown sync lock name: {timerCategoryName}");
                }

                DateTime? max = Models.WorkflowRuntime.GetMaxNextTime(connection, _runtime.Id, nextTimeColumnName);

                DateTime result = _runtime.RuntimeDateTimeNow;

                if (max > result)
                {
                    result = max.Value;
                }

                result += TimeSpan.FromMilliseconds(timerInterval);

                using (MySqlTransaction transaction = connection.BeginTransaction())
                {
                    var newLock = Guid.NewGuid();

                    Models.WorkflowRuntime.UpdateNextTime(connection, _runtime.Id, nextTimeColumnName, result, transaction);
                    var rowCount = Models.WorkflowSync.UpdateLock(connection, timerCategoryName, syncLock.Lock, newLock, transaction);

                    if (rowCount == 0)
                    {
                        transaction.Rollback();
                        return null;
                    }

                    transaction.Commit();
                }

                return result;
            }
        }

        public int SendRuntimeLastAliveSignal()
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.SendRuntimeLastAliveSignal(connection, _runtime.Id, _runtime.RuntimeDateTimeNow);
            }
        }

        public List<WorkflowRuntimeModel> GetWorkflowRuntimes()
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return Models.WorkflowRuntime.SelectAll(connection).Select(GetModel).ToList();
            }
        }

        private WorkflowRuntimeModel GetModel(Models.WorkflowRuntime result)
        {
            return new WorkflowRuntimeModel
            {
                Lock = result.Lock,
                RuntimeId = result.RuntimeId,
                Status = result.Status,
                RestorerId = result.RestorerId,
                LastAliveSignal = result.LastAliveSignal,
                NextTimerTime = result.NextTimerTime
            };
        }

        public IApprovalProvider GetIApprovalProvider()
        {
            return this;
        }

        #endregion

        #region ISchemePersistenceProvider

        public SchemeDefinition<XElement> GetProcessSchemeByProcessId(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var processInstance = WorkflowProcessInstance.SelectByKey(connection, processId);
                if (processInstance == null)
                    throw new ProcessNotFoundException(processId);

                if (!processInstance.SchemeId.HasValue)
                    throw SchemeNotFoundException.Create(processId, SchemeLocation.WorkflowProcessInstance);

                var schemeDefinition = GetProcessSchemeBySchemeId(processInstance.SchemeId.Value);
                schemeDefinition.IsDeterminingParametersChanged = processInstance.IsDeterminingParametersChanged;
                return schemeDefinition;
            }
        }

        public SchemeDefinition<XElement> GetProcessSchemeBySchemeId(Guid schemeId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessScheme processScheme = WorkflowProcessScheme.SelectByKey(connection, schemeId);

                if (processScheme == null || string.IsNullOrEmpty(processScheme.Scheme))
                    throw SchemeNotFoundException.Create(schemeId, SchemeLocation.WorkflowProcessScheme);

                return ConvertToSchemeDefinition(processScheme);
            }
        }

        public SchemeDefinition<XElement> GetProcessSchemeWithParameters(string schemeCode, string definingParameters,
            Guid? rootSchemeId, bool ignoreObsolete)
        {
            IEnumerable<WorkflowProcessScheme> processSchemes;
            var hash = HashHelper.GenerateStringHash(definingParameters);

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                processSchemes =
                    WorkflowProcessScheme.Select(connection, schemeCode, hash, ignoreObsolete ? false : (bool?)null,
                        rootSchemeId);
            }

            if (!processSchemes.Any())
                throw SchemeNotFoundException.Create(schemeCode, SchemeLocation.WorkflowProcessScheme, definingParameters);

            if (processSchemes.Count() == 1)
            {
                var scheme = processSchemes.First();
                return ConvertToSchemeDefinition(scheme);
            }

            foreach (var processScheme in processSchemes.Where(processScheme => processScheme.DefiningParameters == definingParameters))
            {
                return ConvertToSchemeDefinition(processScheme);
            }

            throw SchemeNotFoundException.Create(schemeCode, SchemeLocation.WorkflowProcessScheme, definingParameters);
        }

        public void SetSchemeIsObsolete(string schemeCode, IDictionary<string, object> parameters)
        {
            var definingParameters = DefiningParametersSerializer.Serialize(parameters);
            var definingParametersHash = HashHelper.GenerateStringHash(definingParameters);

            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessScheme.SetObsolete(connection, schemeCode, definingParametersHash);
            }
        }

        public void SetSchemeIsObsolete(string schemeCode)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowProcessScheme.SetObsolete(connection, schemeCode);
            }
        }

        public SchemeDefinition<XElement> SaveScheme(SchemeDefinition<XElement> scheme)
        {
            var definingParameters = scheme.DefiningParameters;
            var definingParametersHash = HashHelper.GenerateStringHash(definingParameters);


            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var oldSchemes = WorkflowProcessScheme.Select(connection, scheme.SchemeCode, definingParametersHash,
                    scheme.IsObsolete, scheme.RootSchemeId);

                if (oldSchemes.Any())
                {
                    WorkflowProcessScheme existing = oldSchemes.FirstOrDefault(oldScheme => oldScheme.DefiningParameters == definingParameters);
                    if (existing != null)
                    {
                        return ConvertToSchemeDefinition(existing);
                    }
                }

                var newProcessScheme = new WorkflowProcessScheme
                {
                    Id = scheme.Id,
                    DefiningParameters = definingParameters,
                    DefiningParametersHash = definingParametersHash,
                    Scheme = scheme.Scheme.ToString(),
                    SchemeCode = scheme.SchemeCode,
                    RootSchemeCode = scheme.RootSchemeCode,
                    RootSchemeId = scheme.RootSchemeId,
                    AllowedActivities = JsonConvert.SerializeObject(scheme.AllowedActivities),
                    StartingTransition = scheme.StartingTransition,
                    IsObsolete = scheme.IsObsolete
                };

                newProcessScheme.Insert(connection);

                return ConvertToSchemeDefinition(newProcessScheme);
            }
        }

        public void SaveScheme(string schemaCode, bool canBeInlined, List<string> inlinedSchemes, string scheme, List<string> tags)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowScheme wfScheme = WorkflowScheme.SelectByKey(connection, schemaCode);
                if (wfScheme == null)
                {
                    wfScheme = new WorkflowScheme
                    {
                        Code = schemaCode,
                        Scheme = scheme,
                        CanBeInlined = canBeInlined,
                        InlinedSchemes = inlinedSchemes.Any() ? JsonConvert.SerializeObject(inlinedSchemes) : null,
                        Tags = TagHelper.ToTagStringForDatabase(tags)
                    };
                    wfScheme.Insert(connection);
                }
                else
                {
                    wfScheme.Scheme = scheme;
                    wfScheme.CanBeInlined = canBeInlined;
                    wfScheme.InlinedSchemes = inlinedSchemes.Any() ? JsonConvert.SerializeObject(inlinedSchemes) : null;
                    wfScheme.Tags = TagHelper.ToTagStringForDatabase(tags);
                    wfScheme.Update(connection);
                }

            }
        }

        public XElement GetScheme(string code)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var scheme = WorkflowScheme.SelectByKey(connection, code);
                if (scheme == null || string.IsNullOrEmpty(scheme.Scheme))
                    throw SchemeNotFoundException.Create(code, SchemeLocation.WorkflowScheme);

                return XElement.Parse(scheme.Scheme);
            }
        }

        public List<string> GetInlinedSchemeCodes()
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowScheme.GetInlinedSchemeCodes(connection);
            }
        }

        public List<string> GetRelatedByInliningSchemeCodes(string schemeCode)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowScheme.GetRelatedSchemeCodes(connection, schemeCode);
            }
        }

        public List<string> SearchSchemesByTags(params string[] tags)
        {
            return SearchSchemesByTags(tags?.AsEnumerable());
        }

        public List<string> SearchSchemesByTags(IEnumerable<string> tags)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                return WorkflowScheme.GetSchemeCodesByTags(connection, tags);
            }
        }

        public void AddSchemeTags(string schemeCode, params string[] tags)
        {
            AddSchemeTags(schemeCode, tags?.AsEnumerable());
        }

        public void AddSchemeTags(string schemeCode, IEnumerable<string> tags)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowScheme.AddSchemeTags(connection, schemeCode, tags, _runtime.Builder);
            }
        }

        public void RemoveSchemeTags(string schemeCode, params string[] tags)
        {
            RemoveSchemeTags(schemeCode, tags?.AsEnumerable());
        }

        public void RemoveSchemeTags(string schemeCode, IEnumerable<string> tags)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowScheme.RemoveSchemeTags(connection, schemeCode, tags, _runtime.Builder);
            }
        }

        public void SetSchemeTags(string schemeCode, params string[] tags)
        {
            SetSchemeTags(schemeCode, tags?.AsEnumerable());
        }

        public void SetSchemeTags(string schemeCode, IEnumerable<string> tags)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                WorkflowScheme.SetSchemeTags(connection, schemeCode, tags, _runtime.Builder);
            }
        }

        #endregion

        #region IWorkflowGenerator

        public XElement Generate(string schemeCode, Guid schemeId, IDictionary<string, object> parameters)
        {
            if (parameters.Count > 0)
                throw new InvalidOperationException("Parameters not supported");

            return GetScheme(schemeCode);
        }

        #endregion

        #region Bulk methods

        public bool IsBulkOperationsSupported
        {
            get { return false; }
        }

        public async Task BulkInitProcesses(List<ProcessInstance> instances, ProcessStatus status, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public async Task BulkInitProcesses(List<ProcessInstance> instances, List<TimerToRegister> timers, ProcessStatus status, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        #endregion

        private SchemeDefinition<XElement> ConvertToSchemeDefinition(WorkflowProcessScheme workflowProcessScheme)
        {
            return new SchemeDefinition<XElement>(workflowProcessScheme.Id, workflowProcessScheme.RootSchemeId,
                workflowProcessScheme.SchemeCode, workflowProcessScheme.RootSchemeCode,
                XElement.Parse(workflowProcessScheme.Scheme), workflowProcessScheme.IsObsolete, false,
                JsonConvert.DeserializeObject<List<string>>(workflowProcessScheme.AllowedActivities ?? "null"),
                workflowProcessScheme.StartingTransition,
                workflowProcessScheme.DefiningParameters);
        }

        private Tuple<int, WorkflowRuntimeModel> UpdateWorkflowRuntime(WorkflowRuntimeModel runtime, Action<WorkflowRuntimeModel> setter,
            Func<MySqlConnection, WorkflowRuntimeModel, Guid, int> updateMethod)
        {
            using (var connection = new MySqlConnection(ConnectionString))
            {
                Guid oldLock = runtime.Lock;
                setter(runtime);
                runtime.Lock = Guid.NewGuid();

                var cnt = updateMethod(connection, runtime, oldLock);

                if (cnt != 1)
                {
                    return new Tuple<int, WorkflowRuntimeModel>(cnt, null);
                }

                return new Tuple<int, WorkflowRuntimeModel>(cnt, runtime);
            }
        }

        #region IApprovalProvider

        public async Task DropWorkflowInboxAsync(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                WorkflowInbox.DeleteByProcessId(connection, processId);
            }
        }

        public async Task InsertInboxAsync(Guid processId, List<string> newActors)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var inboxItems = newActors.Select(newactor => new WorkflowInbox() {Id = Guid.NewGuid(), IdentityId = newactor, ProcessId = processId})
                    .ToArray();
                WorkflowInbox.InsertAll(connection, inboxItems);
            }
        }

        public async Task WriteApprovalHistoryAsync(Guid id, string currentState, string nextState, string triggerName, string allowedToEmployeeNames,
            long order)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var historyItem = new WorkflowApprovalHistory
                {
                    Id = Guid.NewGuid(),
                    AllowedTo = allowedToEmployeeNames,
                    DestinationState = nextState,
                    ProcessId = id,
                    InitialState = currentState,
                    TriggerName = triggerName,
                    Sort = order
                };

                historyItem.Insert(connection);
            }
        }

        public async Task UpdateApprovalHistoryAsync(Guid id, string currentState, string nextState, string triggerName, string identityId, long order,
            string comment)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                var historyItem = WorkflowApprovalHistory.SelectByProcessId(connection, id).FirstOrDefault(h =>
                    h.ProcessId == id && !h.TransitionTime.HasValue &&
                    h.InitialState == currentState && h.DestinationState == nextState);

                if (historyItem == null)
                {
                    historyItem = new WorkflowApprovalHistory
                    {
                        Id = Guid.NewGuid(),
                        AllowedTo = string.Empty,
                        DestinationState = nextState,
                        ProcessId = id,
                        InitialState = currentState,
                        Sort = order,
                        TriggerName = triggerName,
                        Commentary = comment,
                        TransitionTime = _runtime.RuntimeDateTimeNow,
                        IdentityId = identityId
                    };

                    historyItem.Insert(connection);
                }
                else
                {
                    historyItem.TriggerName = triggerName;
                    historyItem.TransitionTime = _runtime.RuntimeDateTimeNow;
                    historyItem.IdentityId = identityId;
                    historyItem.Commentary = comment;
                    historyItem.Update(connection);
                }
            }
        }

        public async Task DropEmptyApprovalHistoryAsync(Guid processId)
        {
            using (MySqlConnection connection = new MySqlConnection(ConnectionString))
            {
                foreach (var record in WorkflowApprovalHistory.SelectByProcessId(connection, processId).Where(x => !x.TransitionTime.HasValue).ToList())
                {
                    WorkflowApprovalHistory.Delete(connection, record.Id);
                }
            }
        }

        #endregion IApprovalProvider

    }
}
