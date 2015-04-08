﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Xsl;
using System.IO;
using System.Diagnostics;

namespace Aras.Tools.InnovatorAdmin
{
  public class ExportProcessor : IProgressReporter
  {
    XslCompiledTransform _queryTransform;
    XslCompiledTransform _resultTransform;

    private Connection _conn;
    private DependencyAnalyzer _dependAnalyzer;
    private Dictionary<string, ItemType> _itemTypes;

    public event EventHandler<ActionCompleteEventArgs> ActionComplete;
    public event EventHandler<ProgressChangedEventArgs> ProgressChanged; 

    public ExportProcessor(Func<string, XmlNode, XmlNode> applyAction)
    {
      _conn = new Connection(applyAction);
    }

    //public void Export(InstallScript script, string doc)
    //{
    //  var input = new XmlDocument();
      
    //  // Perform the transforms
    //  input.LoadXml("<AML>" + doc + "</AML>");
    //  XmlNode node = input.DocumentElement;
    //  while (node != null && node.LocalName == "AML") node = node.Elements().FirstOrDefault();
    //  if (node == null) return;
    //  node = node.ParentNode;

    //  Export(script, node);
    //}

    public void Export(InstallScript script, IEnumerable<ItemReference> items)
    {
      ReportProgress(0, "Loading system data");
      EnsureSystemData();

      var uniqueItems = new HashSet<ItemReference>(items);
      if (script.Lines != null) uniqueItems.ExceptWith(script.Lines.Select(l => l.Reference));
      var itemList = uniqueItems.ToList();

      ItemType metaData;
      var outputDoc = new XmlDocument();
      outputDoc.AppendChild(outputDoc.CreateElement("AML"));
      XmlElement queryElem;
      var whereClause = new StringBuilder();

      // Convert polyitems to their actual item types
      ItemType realType;
      ItemReference newRef;
      foreach (var typeItems in (from i in itemList
                                group i by i.Type into typeGroup
                                select typeGroup).ToList())
      {
        if (_itemTypes.TryGetValue(typeItems.Key.ToLowerInvariant(), out metaData) && metaData.IsPolymorphic)
        {
          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", typeItems.Key);

          if (typeItems.Any(i => i.Unique.IsGuid()))
          {
            whereClause.Append("[")
              .Append(typeItems.Key.Replace(' ', '_'))
              .Append("].[id] in ('")
              .Append(typeItems.Where(i => i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + "','" + c))
              .Append("')");
          }
          if (typeItems.Any(i => !i.Unique.IsGuid()))
          {
            whereClause.AppendSeparator(" or ",
              typeItems.Where(i => !i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + " or " + c));
          }
          queryElem.SetAttribute("where", whereClause.ToString());


          // Remove the items from the list
          var idx = 0;
          while (idx < itemList.Count)
          {
            if (itemList[idx].Type == typeItems.Key)
            {
              itemList.RemoveAt(idx);
            }
            else 
            {
              idx++;
            }
          }

          var polyItems = _conn.GetItems("ApplyItem", queryElem.OuterXml);
          foreach (var polyItem in polyItems)
          {
            newRef = ItemReference.FromFullItem(polyItem, true);
            realType = _itemTypes.Values.FirstOrDefault(t => t.Id == polyItem.Element("itemtype", ""));
            if (realType != null) newRef.Type = realType.Name;
            itemList.Add(newRef);
          }
        }
      }


      foreach (var typeItems in (from i in itemList
                                group i by i.Type into typeGroup
                                select typeGroup))
      {
        whereClause.Length = 0;

        if (_itemTypes.TryGetValue(typeItems.Key.ToLowerInvariant(), out metaData) && metaData.IsVersionable)
        {
          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", typeItems.Key);

          if (typeItems.Any(i => i.Unique.IsGuid()))
          {
            whereClause.Append("[")
              .Append(typeItems.Key.Replace(' ', '_'))
              .Append("].[config_id] in (select config_id from innovator.[")
              .Append(typeItems.Key.Replace(' ', '_'))
              .Append("] where id in ('")
              .Append(typeItems.Where(i => i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + "','" + c))
              .Append("'))");
          }
          if (typeItems.Any(i => !i.Unique.IsGuid()))
          {
            whereClause.AppendSeparator(" or ",
              typeItems.Where(i => !i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + " or " + c));
          }
          queryElem.SetAttribute("where", whereClause.ToString());

          outputDoc.DocumentElement.AppendChild(queryElem);
        }
        else if (typeItems.Key == "ItemType")
        {
          // Make sure relationship item types aren't accidentally added
          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", typeItems.Key);

          if (typeItems.Any(i => i.Unique.IsGuid()))
          {
            whereClause.Append("[ItemType].[id] in ('")
              .Append(typeItems.Where(i => i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + "','" + c))
              .Append("')");
          }
          if (typeItems.Any(i => !i.Unique.IsGuid()))
          {
            whereClause.AppendSeparator(" or ",
              typeItems.Where(i => !i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + " or " + c));
          }
          queryElem.SetAttribute("where", "(" + whereClause.ToString() + ") and [ItemType].[is_relationship] = '0'");

          outputDoc.DocumentElement.AppendChild(queryElem);

          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", "RelationshipType");
          queryElem.SetAttribute("where", "[RelationshipType].[relationship_id] in (select id from innovator.[ItemType] where " + whereClause.ToString() + ")");

          outputDoc.DocumentElement.AppendChild(queryElem);
        }
        else if (typeItems.Key == "List")
        {
          // Filter out auto-generated lists for polymorphic item types
          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", typeItems.Key);

          if (typeItems.Any(i => i.Unique.IsGuid()))
          {
            whereClause.Append("[List].[id] in ('")
              .Append(typeItems.Where(i => i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + "','" + c))
              .Append("')");
          }
          if (typeItems.Any(i => !i.Unique.IsGuid()))
          {
            whereClause.AppendSeparator(" or ",
              typeItems.Where(i => !i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + " or " + c));
          }

          queryElem.SetAttribute("where", "(" + whereClause.ToString() + ") " + Properties.Resources.ListSqlCriteria );
          outputDoc.DocumentElement.AppendChild(queryElem);
        }
        else
        {
          queryElem = outputDoc.CreateElement("Item");
          queryElem.SetAttribute("action", "get");
          queryElem.SetAttribute("type", typeItems.Key);

          if (typeItems.Any(i => i.Unique.IsGuid()))
          {
            whereClause.Append("[")
              .Append(typeItems.Key.Replace(' ', '_'))
              .Append("].[id] in ('")
              .Append(typeItems.Where(i => i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + "','" + c))
              .Append("')");
          }
          if (typeItems.Any(i => !i.Unique.IsGuid()))
          {
            whereClause.AppendSeparator(" or ",
              typeItems.Where(i => !i.Unique.IsGuid()).Select(i => i.Unique).Aggregate((p, c) => p + " or " + c));
          }

          queryElem.SetAttribute("where", whereClause.ToString());

          outputDoc.DocumentElement.AppendChild(queryElem);
        }
      }

      Export(script, outputDoc.DocumentElement);
    }

    private void Export(InstallScript script, XmlNode input)
    {
      try
      {
        //Identify dependency level on Relationship Types (number of levels to get to a real itemtype)
        //Get property items for form fields
        ReportProgress(0, "Loading system data");
        EnsureSystemData();
      
        var doc = TransformExportQuery(input);
        FixFederatedRelationships(doc.DocumentElement);
        var result = ExecuteExportQuery(doc.DocumentElement);
        ExpandVersionableRefs(result);
        
        // Add warnings for embedded relationships
        var warnings = new HashSet<ItemReference>(); 
        ItemReference warning;
        foreach (var relType in result.ElementsByXPath("/Result/Item[@type='ItemType']/Relationships/Item[@type='RelationshipType']"))
        {
          warning = ItemReference.FromFullItem(relType as XmlElement, true);
          warning.KeyedName = "* Possible missing relationship: " + warning.KeyedName;
          warnings.Add(warning);
        }

        doc = TransformResults(result.DocumentElement);
        ExpandSystemIdentities(doc);
        NormalizeClassStructure(doc);
        ReorderForeignProperties(doc);
        RemoveRelatedItems(doc);
        RemoveKeyedNameAttributes(doc);

        //var sqlNames = new HashSet<string>();
        //foreach (var prop in result.ElementsByXPath("//Item[@type='SQL']/sqlserver_body"))
        //{
        //  sqlNames.UnionWith(Utils.FindSqlServerNamesBySchema(prop.InnerText, "innovator")
        //    .Where(n => string.Compare(n, prop.Parent().Element("name", ""), StringComparison.OrdinalIgnoreCase) != 0)
        //    .Select(n => n.ToLowerInvariant()));
        //}
        //ItemType sqlType;
        //foreach (var name in sqlNames)
        //{
        //  if (_itemTypes.TryGetValue(name.Replace('_', ' '), out sqlType))
        //  {
        //    warning = sqlType.Reference.Clone();
        //    warning.KeyedName = "* Possible missing item type: " + warning.KeyedName;
        //  }
        //  else
        //  {
        //    warning = new ItemReference("SQL", "[SQL].[name] = '" + name + "'");
        //    warning.KeyedName = "* Possible missing SQL: " + name;
        //  }
        //  warnings.Add(warning);
        //}

        Export(script, doc, warnings);
      }
      catch (Exception ex)
      {
        this.OnActionComplete(new ActionCompleteEventArgs() { Exception = ex });
      }
    }

    public void Export(InstallScript script, XmlDocument doc, HashSet<ItemReference> warnings = null) {
      try 
      {
        EnsureSystemData();
        _dependAnalyzer.Reset();
        FixCyclicalWorkflowLifeCycleRefs(doc);
        FixCyclicalWorkflowItemTypeRefs(doc);
        MoveFormRefsInline(doc);

        // Sort the resulting nodes as appropriate
        ReportProgress(98, "Sorting the results");
        XmlNode itemNode = doc.DocumentElement;
        while (itemNode != null && itemNode.LocalName != "Item") itemNode = itemNode.Elements().FirstOrDefault();
        if (itemNode == null) throw new InvalidOperationException(); //TODO: Give better error information here (e.g. interpret an error item if present)

        var newInstallItems = (from e in itemNode.ParentNode.Elements()
                               where e.LocalName == "Item" && e.HasAttribute("type")
                               select InstallItem.FromScript(e)).ToList();
        foreach (var newInstallItem in newInstallItems)
        {
          _dependAnalyzer.GatherDependencies(newInstallItem.Script, newInstallItem.Reference, newInstallItem.CoreDependencies);
        }
        _dependAnalyzer.CleanDependencies();

        var results = GetDependencyList((script.Lines ?? Enumerable.Empty<InstallItem>())
                                        .Concat(newInstallItems)).ToList();

        if (warnings == null) warnings = new HashSet<ItemReference>();
        warnings.ExceptWith(results.Select(r => r.Reference));
        
        script.Lines = warnings.Select(w => InstallItem.FromWarning(w, w.KeyedName))
          .Concat(results.Where(r => r.Type == InstallType.DependencyCheck || r.Type == InstallType.Warning))
          .OrderBy(r => r.Name)
          .ToList()
          .Concat(results.Where(r => r.Type != InstallType.DependencyCheck && r.Type != InstallType.Warning))
          .ToList();
        this.OnActionComplete(new ActionCompleteEventArgs());
      }
      catch (Exception ex)
      {
        this.OnActionComplete(new ActionCompleteEventArgs() { Exception = ex });
      }
    }

    /// <summary>
    /// Transform the search query to provide the appropriate levels commands, etc. to ensure all data is loaded
    /// </summary>
    private XmlDocument TransformExportQuery(XmlNode input)
    {
      ReportProgress(2, "Normalizing search for data");
      var doc = new XmlDocument();
      EnsureTransforms();
      using (var reader = new XmlNodeReader(input))
      {
        using (var queryWriter = new StringWriter())
        {
          _queryTransform.Transform(reader, null, queryWriter);
          queryWriter.Flush();
          doc.LoadXml(queryWriter.ToString());
        }
      }
      return doc;
    }
    /// <summary>
    /// Fix the queries for items with federated relationships
    /// </summary>
    private void FixFederatedRelationships(XmlNode query)
    {
      ItemType itemType;
      string relQuery;
      foreach (var item in query.ChildNodes.OfType<XmlElement>())
      {
        if (item.Attribute("levels") == "1" 
          && _itemTypes.TryGetValue(item.Attribute("type").ToLowerInvariant(), out itemType) 
          && itemType.Relationships.Any(r => r.IsFederated))
        {
          item.RemoveAttribute("levels");
          if (itemType.Relationships.Any(r => !r.IsFederated))
          {
            relQuery = (from r in itemType.Relationships
                         where !r.IsFederated
                         select string.Format("<Item type=\"{0}\" action=\"get\" />", r.Name))
                        .Aggregate((p, c) => p + c);
            item.InnerXml = "<Relationships>" + relQuery + "</Relationships>";
          }
        }
      }
    }
    /// <summary>
    /// Execute the query to get all the relevant data from the database regarding the items to export.
    /// </summary>
    private XmlDocument ExecuteExportQuery(XmlNode query)
    {
      var result = new XmlDocument();
      result.AppendChild(result.CreateElement("Result"));
      IEnumerable<XmlElement> resultItems;
      var queryItems = query.Elements("Item").ToList();
      var i = 0;
      ReportProgress(4, string.Format("Searching for data ({0} of {1})", i, queryItems.Count));
      foreach (var item in queryItems)
      {
        resultItems = _conn.GetItems("ApplyItem", item);
        foreach (var resultItem in resultItems)
        {
          result.DocumentElement.AppendChild(result.ImportNode(resultItem, true));
        }
        i++;
        ReportProgress(4 + (int)(i * 90.0 / queryItems.Count), string.Format("Searching for data ({0} of {1})", i, queryItems.Count));
      }
      return result;
    }
    /// <summary>
    /// Expand versionable property references
    /// </summary>
    private void ExpandVersionableRefs(XmlDocument result)
    {
      // TODO: only do this with properties that are supposed to float.
      IEnumerable<XmlElement> resultItems;
      foreach (var itemType in _itemTypes.Values.Where(t => t.IsVersionable))
      {
        foreach (var item in result.ElementsByXPath("//Item[@type='" + itemType.Name + "']"))
        {
          item.SetAttribute("_is_versionable", "1");
        }

        var ids = result.ElementsByXPath("//self::node()[local-name() != 'Item' and local-name() != 'id' and local-name() != 'config_id' and local-name() != 'source_id' and @type='" + itemType.Name + "' and not(Item)]").Select(e => e.InnerText).Distinct();
        if (ids.Any())
        {
          resultItems = _conn.GetItems("ApplyItem",
            "<Item type=\"" + itemType.Name + "\" action=\"get\" select=\"config_id\" idlist=\""
          + ids.Aggregate((p, c) => p + "," + c)
          + "\" />");
          var versMeta = resultItems.Select(r =>
          {
            r.SetAttribute("_is_versionable", "1");
            r.SetAttribute("action", "get");
            return r;
          }).ToDictionary(r => r.Attribute("id"), r => r.OuterXml);
          string versItem;
          foreach (var versProp in result.ElementsByXPath("//self::node()[local-name() != 'Item' and local-name() != 'id' and local-name() != 'config_id' and local-name() != 'source_id' and @type='" + itemType.Name + "' and not(Item)]"))
          {
            if (versMeta.TryGetValue(versProp.InnerText, out versItem))
            {
              versProp.InnerXml = versItem;
            }
          }
        }
      }
    }
    /// <summary>
    /// Transform the results to normalize the output using the XSLT
    /// </summary>
    private XmlDocument TransformResults(XmlNode results)
    {
      var doc = new XmlDocument();
      // Transform the output
      ReportProgress(94, "Transforming the results");
      using (var output = new StringWriter())
      {
        using (var reader = new XmlNodeReader(results))
        {
          _resultTransform.Transform(reader, null, output);
          output.Flush();
        }
        doc.LoadXml(output.ToString());
      }
      return doc;
    }
    /// <summary>
    /// Normalize the formatting of class structure nodes to aid in manual diff-ing
    /// </summary>
    private void NormalizeClassStructure(XmlDocument doc)
    {
      ReportProgress(97, "Transforming the results");
      var settings = new XmlWriterSettings();
      settings.OmitXmlDeclaration = true;
      settings.Indent = true;
      settings.IndentChars = "  ";
      var classStructs = (from e in doc.ElementsByXPath("//Item[@type='ItemType']/class_structure")
                          where !string.IsNullOrEmpty(e.InnerText)
                          select e);
      XmlDocument classDoc;
      foreach (var classStruct in classStructs)
      {
        classDoc = new XmlDocument();
        classDoc.LoadXml(classStruct.InnerText);
        using (var output = new StringWriter())
        {
          using (var writer = XmlTextWriter.Create(output, settings))
          {
            WriteClassStruct(Enumerable.Repeat((XmlNode)classDoc.DocumentElement, 1), writer);
          }
          classStruct.InnerXml = "<![CDATA[" + output.ToString() + "]]>";
        }
      }
    }
    /// <summary>
    /// Fix cyclical workflow-life cycle references by creating an edit script
    /// </summary>
    private void FixCyclicalWorkflowLifeCycleRefs(XmlDocument doc)
    {
      var workflowRefs = doc.ElementsByXPath("//Item[@type='Life Cycle State' and (@action='add' or @action='merge' or @action='create')]/workflow");
      XmlElement map;
      XmlElement fix;
      foreach (var workflowRef in workflowRefs)
      {
        fix = (XmlElement)workflowRef.ParentNode.CloneNode(false);
        fix.SetAttribute("action", "edit");
        fix.IsEmpty = true;
        fix.AppendChild(workflowRef.CloneNode(true));
        map = workflowRef.Parents().First(e => e.LocalName == "Item" && e.Attribute("type", "") == "Life Cycle Map");
        map.ParentNode.InsertAfter(fix, map);
        workflowRef.ParentNode.RemoveChild(workflowRef);
      }
    }
    /// <summary>
    /// Move the form nodes inline with the itemtype create script
    /// </summary>
    private void MoveFormRefsInline(XmlDocument doc)
    {
      foreach (var form in doc.ElementsByXPath("//Item[@type='Form' and @action and @id]"))
      {
        var references = doc.ElementsByXPath("//self::node()[local-name() != 'Item' and local-name() != 'id' and local-name() != 'config_id' and @type='Form' and not(Item) and text() = '" + form.Attribute("id", "") + "']");
        if (references.Any())
        {
          foreach (var formRef in references)
          {
            formRef.InnerXml = form.OuterXml;
            foreach (var relTag in formRef.Elements("Item").Elements("Relationships").ToList())
            {
              relTag.Detatch();
            }
          }
        }
      }
    }
    /// <summary>
    /// Fix cyclical workflow-itemtype references by creating an edit script
    /// </summary>
    private void FixCyclicalWorkflowItemTypeRefs(XmlDocument doc)
    {
      var workflowRefs = doc.ElementsByXPath("//Item/Relationships/Item[@type='Allowed Workflow' and (@action='add' or @action='merge' or @action='create')]");
      XmlElement type;
      XmlElement sourceId;
      foreach (var workflowRef in workflowRefs)
      {
        if (!workflowRef.Elements("source_id").Any())
        {
          type = (XmlElement)workflowRef.ParentNode.ParentNode;
          sourceId = workflowRef.AppendElement("source_id");
          sourceId.SetAttribute("type", type.Attribute("type"));
          sourceId.SetAttribute("keyed_name", type.Element("id").Attribute("keyed_name", ""));
          sourceId.InnerText = type.Attribute("id");
        }
        type = workflowRef.Parents().Last(e => e.LocalName == "Item");
        workflowRef.ParentNode.RemoveChild(workflowRef);
        type.ParentNode.InsertAfter(workflowRef, type);
      }
    }
    /// <summary>
    /// Reorder properties so that foreign properties are dealt with last due to dependencies
    /// </summary>
    private void ReorderForeignProperties(XmlDocument doc)
    {
      var relsToReorder = doc.ElementsByXPath("//Relationships[Item/@type = 'Property' and Item/data_type = 'foreign']");
      List<XmlElement> props;

      foreach (var relToReorder in relsToReorder)
      {
        props = relToReorder
          .Elements(e => e.LocalName == "Item" && e.Attribute("type") == "Property")
          .OrderByDescending(e => (e.Element("data_type", "") == "foreign" ? 1 : 0))
          .ThenByDescending(e => e.Element("name", ""))
          .ToList();
        foreach (var prop in props)
        {
          prop.ParentNode.RemoveChild(prop);
          relToReorder.InsertBefore(prop, relToReorder.FirstChild);
        }
      }
    }
    /// <summary>
    /// Match system identities by name instead of ID
    /// </summary>
    private void ExpandSystemIdentities(XmlDocument doc)
    {
      ItemReference ident;
      foreach (var elem in doc.ElementsByXPath("//self::node()[local-name() != 'Item' and local-name() != 'id' and local-name() != 'config_id' and local-name() != 'source_id' and @type='Identity' and not(Item)]"))
      {
        ident = _dependAnalyzer.GetSystemIdentity(elem.InnerText);
        if (ident != null)
        {
          elem.InnerXml = "<Item type=\"Identity\" action=\"get\" select=\"id\"><name>" + ident.KeyedName + "</name></Item>";
        }
      }
    }
    /// <summary>
    /// Remove related items from relationships to non-core itemtypes
    /// </summary>
    private void RemoveRelatedItems(XmlDocument doc)
    {
      ItemType itemType;
      foreach (var elem in doc.ElementsByXPath("/AML/Item/Relationships/Item/related_id[Item and not(Item/@action = 'get')]"))
      {
        if (!_itemTypes.TryGetValue(elem.Parent().Parent().Parent().Attribute("type").ToLowerInvariant(), out itemType) || !itemType.IsCore)
        {
          elem.InnerXml = elem.Element("Item").Attribute("id");
        }
      }
    }
    /// <summary>
    /// Remove keyed_name attributes to make diffing easier
    /// </summary>
    private void RemoveKeyedNameAttributes(XmlDocument doc)
    {
      foreach (XmlNode attr in doc.SelectNodes("@keyed_name"))
      {
        attr.Detatch();
      }
    }


    public void RemoveReferencingItems(InstallScript script, ItemReference itemRef)
    {
      var nodes = _dependAnalyzer.RemoveDependencyContexts(itemRef);
      script.Lines = script.Lines.Where(l => !(l.Type == InstallType.Create || l.Type == InstallType.Script) || !nodes.Contains(l.Script)).ToList();
    }
    public void RemoveReferences(InstallScript script, ItemReference itemRef)
    {
      var nodes = _dependAnalyzer.RemoveDependencyReferences(itemRef);
      script.Lines = script.Lines.Where(l => !(l.Type == InstallType.Create || l.Type == InstallType.Script) || !nodes.Contains(l.Script)).ToList();
    }

    public IEnumerable<ItemReference> NormalizeRequest(IEnumerable<ItemReference> items)
    {
      foreach (var item in items)
      {
        if (item.Unique.IsGuid())
        {
          switch (item.Type)
          {
            case "Life Cycle State":
              yield return new ItemReference("Life Cycle Map", "[Life_Cycle_Map].[id] in (select source_id from [innovator].[Life_Cycle_State] where id = '" + item.Unique +"')");
              break;
            case "Life Cycle Transition":
              yield return new ItemReference("Life Cycle Map", "[Life_Cycle_Map].[id] in (select source_id from [innovator].[Life_Cycle_Transition] where id = '" + item.Unique + "')");
              break;
            case "Property":
              yield return new ItemReference("ItemType", "[ItemType].[id] in (select source_id from [innovator].[Property] where id = '" + item.Unique + "')");
              break;
            case "Activity":
              yield return new ItemReference("Workflow Map", "[Workflow_Map].[id] in (select source_id from [innovator].[Workflow_Map_Activity] where related_id = '" + item.Unique + "')");
              break;
            default:
              yield return item;
              break;
          }
        }
        else
        {
          yield return item;
        }
      }
    }

    protected virtual void ReportProgress(int progress, string message)
    {
      OnProgressChanged(new ProgressChangedEventArgs(message, progress));
    }
    protected virtual void OnActionComplete(ActionCompleteEventArgs e)
    {
      ActionComplete(this, e);
    }
    protected virtual void OnProgressChanged(ProgressChangedEventArgs e)
    {
      ProgressChanged(this, e);
    }

    private IEnumerable<InstallItem> GetDependencyList(IEnumerable<InstallItem> values)
    {
      var lookup = (from i in values
                   group i by i.Reference into refGroup
                   select refGroup)
                  .ToDictionary(i => i.Key, i => (IEnumerable<InstallItem>)i);

      IEnumerable<ItemReference> sorted = null;
      IList<ItemReference> cycle = new List<ItemReference>() { null };
      var loops = 0;

      while (cycle.Count > 0 && loops < 10)
      {
        cycle.Clear();
        loops++;

        sorted = lookup.Keys.DependencySort<ItemReference>(d =>
        {
          IEnumerable<InstallItem> res = null;
          if (lookup.TryGetValue(d, out res)) return res.SelectMany(r =>
          {
            var ii = r as InstallItem;
            if (ii == null) return Enumerable.Empty<ItemReference>();
            return _dependAnalyzer.GetDependencies(ii.Reference);
          });
          return Enumerable.Empty<ItemReference>();
        }, ref cycle, false);

        // Resolve cycle somehow
      }


      IEnumerable<InstallItem> items = null;
      foreach (var sort in sorted)
      {
        if (lookup.TryGetValue(sort, out items))
        {
          foreach (var item in items)
            yield return item;
        }
        else
        {
          yield return InstallItem.FromDependency(sort);
        }
      }
    }

    /// <summary>
    /// Normalize the class structure to make it easier to handle in a text compare
    /// </summary>
    /// <param name="nodes">Nodes to render</param>
    /// <param name="writer">Writer to render the nodes to</param>
    private void WriteClassStruct(IEnumerable<XmlNode> nodes, XmlWriter writer)
    {
      // Reorder the elements
      var nodeList = nodes.ToList();
      var indices = (from n in nodeList.Select((n, i) => new { Node = n, Index = i })
                     where n.Node is XmlElement
                     orderby n.Index
                     select n.Index);
      var elems = (from n in nodeList.OfType<XmlElement>()
                   orderby (n.Attributes["id"] == null ? string.Empty : n.Attributes["id"].Value)
                   select n).ToList();
      var indiceEnum = indices.GetEnumerator();
      var elemEnum = elems.GetEnumerator();
      while (indiceEnum.MoveNext() && elemEnum.MoveNext())
      {
        nodeList[indiceEnum.Current] = elemEnum.Current;
      }

      XmlElement elem;
      foreach (var node in nodeList)
      {
        elem = node as XmlElement;
        if (elem == null)
        {
          node.WriteTo(writer);
        }
        else
        {
          writer.WriteStartElement(elem.Prefix, elem.LocalName, elem.NamespaceURI);
          if (elem.HasAttributes)
          {
            // Reorder the attributes
            foreach (var attr in (from a in elem.Attributes.OfType<XmlAttribute>()
                                  orderby a.Name
                                  select a))
            {
              attr.WriteTo(writer);
            }
          }
          WriteClassStruct(elem.ChildNodes.OfType<XmlNode>(), writer);
          writer.WriteEndElement();
        }
        switch (node.NodeType)
        {
          case XmlNodeType.Element:

            break;
          default:
            node.WriteTo(writer);
            break;
        }
      }
    }

    private void EnsureTransforms()
    {
      if (_queryTransform == null)
      {
        _queryTransform = new XslCompiledTransform();
        using (Stream stream = this.GetType().Assembly.GetManifestResourceStream("Aras.Tools.InnovatorAdmin.Export.ExportQuery.xslt"))
        {
          using (var xslt = XmlReader.Create(stream))
          {
            _queryTransform.Load(xslt);
          }
        }
      }

      if (_resultTransform == null)
      {
        _resultTransform = new XslCompiledTransform();
        using (Stream stream = this.GetType().Assembly.GetManifestResourceStream("Aras.Tools.InnovatorAdmin.Export.ExportResult.xslt"))
        {
          using (var xslt = XmlReader.Create(stream))
          {
            _resultTransform.Load(xslt);
          }
        }
      }
    }

    private void EnsureSystemData()
    {
      if (_itemTypes == null)
      {
        EnsureSystemData(_conn, ref _itemTypes);
        
        _dependAnalyzer = new DependencyAnalyzer(_conn, _itemTypes); 
      }
    }

    internal static void EnsureSystemData(Connection _conn, ref Dictionary<string, ItemType> _itemTypes)
    {
      if (_itemTypes == null)
      {
        _itemTypes = new Dictionary<string, ItemType>();
        var itemTypes = _conn.GetItems("ApplyItem", Properties.Resources.ItemTypeData);
        ItemType result;
        foreach (var itemTypeData in itemTypes)
        {
          result = new ItemType();
          result.Id = itemTypeData.Attribute("id");
          result.IsCore = itemTypeData.Element("core").InnerText == "1";
          result.IsFederated = itemTypeData.Element("implementation_type").InnerText == "federated";
          result.IsPolymorphic = itemTypeData.Element("implementation_type").InnerText == "polymorphic";
          result.IsVersionable = itemTypeData.Element("is_versionable").InnerText != "0";
          result.Name = itemTypeData.Element("name").InnerText;
          result.Reference = ItemReference.FromFullItem(itemTypeData, true);
          _itemTypes[result.Name.ToLowerInvariant()] = result;
        }

        var relationships = _conn.GetItems("ApplyItem", Properties.Resources.RelationshipData);
        ItemType relType;
        foreach (var rel in relationships)
        {
          if (rel.Element("source_id").Attribute("name") != null 
            && _itemTypes.TryGetValue(rel.Element("source_id").Attribute("name").ToLowerInvariant(), out result)
            && rel.Element("relationship_id").Attribute("name") != null
            && _itemTypes.TryGetValue(rel.Element("relationship_id").Attribute("name").ToLowerInvariant(), out relType))
          {
            result.Relationships.Add(relType);
          }
        }
      }
    }
  }
}
