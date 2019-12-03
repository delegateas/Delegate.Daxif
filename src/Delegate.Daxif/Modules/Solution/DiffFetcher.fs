﻿module DG.Daxif.Modules.Solution.DiffFetcher

open DG.Daxif.Common
open InternalUtility
open System.IO
open System.IO.Compression
open System
open Microsoft.Xrm.Sdk.Query
open System.Threading
open System.Xml
open Microsoft.Xrm.Sdk
open Microsoft.Xrm.Sdk.Messages
open Microsoft.Xrm.Sdk.Metadata


let fetchSolution proxy (solution: string) = 
  let columnSet = ColumnSet("uniquename", "friendlyname", "publisherid", "version")
  CrmDataInternal.Entities.retrieveSolution proxy solution columnSet

let downloadSolution (env: DG.Daxif.Environment) file_location sol_name minutes =
  let usr, pwd, domain = env.getCreds()
  let ac = CrmAuth.getCredentials env.apToUse usr pwd domain
  let ac' = CrmAuth.getCredentials env.apToUse usr pwd domain

  log.Verbose "Exporting extended solution %A" (file_location + sol_name)
  SolutionHelper.exportWithExtendedSolution' env.url ac ac' sol_name file_location false (DG.Daxif.ConsoleLogger DG.Daxif.LogLevel.Verbose)
  file_location + sol_name

let rec downloadSolutionRetry (env: DG.Daxif.Environment) file_location sol_name minutes retry_count =
  if retry_count > 0 then
    try
      downloadSolution env file_location sol_name minutes
    with _ ->
      downloadSolutionRetry env file_location sol_name minutes retry_count
  else
    downloadSolution env file_location sol_name minutes

let unzip file =
  log.Verbose "Unpacking zip '%s' to '%s'" (file + ".zip") file;
  if Directory.Exists(file) then
    Directory.Delete(file, true) |> ignore;
  ZipFile.ExtractToDirectory(file + ".zip", file);

let fetchSolutionComponents proxy (solutionid: Guid) =
  let query = QueryExpression("solutioncomponent")
  query.ColumnSet <- ColumnSet("componenttype", "objectid");
  query.Criteria <- FilterExpression();
  query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionid.ToString("B"));
  CrmDataHelper.retrieveMultiple proxy query

let fetchSitemapId (proxy: IOrganizationService) (unique_name: string) = 
  let query = QueryExpression("sitemap")
  query.ColumnSet <- ColumnSet(false);
  query.Criteria <- FilterExpression();
  query.Criteria.AddCondition("sitemapnameunique", ConditionOperator.Equal, unique_name);
  CrmDataHelper.retrieveMultiple proxy query
  |> Seq.head
  |> fun a -> a.Id

let fetchAppModuleId (proxy: IOrganizationService) (unique_name: string) = 
  let query = QueryExpression("appmodule")
  query.ColumnSet <- ColumnSet(false);
  query.Criteria <- FilterExpression();
  query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, unique_name);
  CrmDataHelper.retrieveMultiple proxy query
  |> Seq.head
  |> fun a -> a.Id

let fetchImportStatusOnce proxy (id: Guid) =
  let query = QueryExpression("importjob")
  query.NoLock <- true;
  query.ColumnSet <- ColumnSet("progress", "completedon", "data");
  query.Criteria <- FilterExpression();
  query.Criteria.AddCondition("importjobid", ConditionOperator.Equal, id);
  CrmDataHelper.retrieveMultiple proxy query
  |> Seq.tryHead
  |> Option.map (fun a -> 
    a.Attributes.Contains("completedon"), 
    a.Attributes.["progress"] :?> double, 
    a.Attributes.["data"] :?> string)

let fetchWorkflowIds proxy (solutionid: Guid) =
  let le = LinkEntity()
  le.JoinOperator <- JoinOperator.Inner;
  le.LinkFromAttributeName <- @"workflowid";
  le.LinkFromEntityName <- @"workflow";
  le.LinkToAttributeName <- @"objectid";
  le.LinkToEntityName <- @"solutioncomponent";
  le.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionid);
  let q = QueryExpression("workflow")
  q.LinkEntities.Add(le);
  q.Criteria <- FilterExpression ();
  q.Criteria.AddCondition("type", ConditionOperator.Equal, 1);
  CrmDataHelper.retrieveMultiple proxy q
  |> Seq.toList
  |> List.map (fun e -> e.Id)


let rec assocRightOption key map = 
  match map with
    | [] -> None
    | (v, k) :: map' ->
      if k = key
      then Some v
      else assocRightOption key map'

let assoc_right key map = 
  (assocRightOption key map).Value

let fetchGlobalOptionSet (proxy: IOrganizationService) name =
  let req = RetrieveOptionSetRequest ()
  req.Name <- name;
  let resp = proxy.Execute(req) :?> RetrieveOptionSetResponse
  let oSetMeta = resp.OptionSetMetadata :?> OptionSetMetadata
  oSetMeta.Options
  |> Seq.map (fun o -> (o.Label.UserLocalizedLabel.Label, o.Value.Value))
  |> Seq.toList

let fetchComponentType proxy = fetchGlobalOptionSet proxy "componenttype"