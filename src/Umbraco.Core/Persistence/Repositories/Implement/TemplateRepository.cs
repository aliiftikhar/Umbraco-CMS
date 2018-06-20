﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NPoco;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Entities;
using Umbraco.Core.Persistence.Dtos;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Scoping;
using Umbraco.Core.Strings;

namespace Umbraco.Core.Persistence.Repositories.Implement
{
    /// <summary>
    /// Represents the Template Repository
    /// </summary>
    internal class TemplateRepository : NPocoRepositoryBase<int, ITemplate>, ITemplateRepository
    {
        private readonly IFileSystem _masterpagesFileSystem;
        private readonly IFileSystem _viewsFileSystem;
        private readonly ITemplatesSection _templateConfig;
        private readonly ViewHelper _viewHelper;
        private readonly MasterPageHelper _masterPageHelper;

        public TemplateRepository(IScopeAccessor scopeAccessor, CacheHelper cache, ILogger logger, ITemplatesSection templateConfig,
                IFileSystem masterpageFileSystem,
                IFileSystem viewFileSystem)
            : base(scopeAccessor, cache, logger)
        {
            _masterpagesFileSystem = masterpageFileSystem;
            _viewsFileSystem = viewFileSystem;
            _templateConfig = templateConfig;
            _viewHelper = new ViewHelper(_viewsFileSystem);
            _masterPageHelper = new MasterPageHelper(_masterpagesFileSystem);
        }

        protected override IRepositoryCachePolicy<ITemplate, int> CreateCachePolicy()
        {
            return new FullDataSetRepositoryCachePolicy<ITemplate, int>(GlobalIsolatedCache, ScopeAccessor, GetEntityId, /*expires:*/ false);
        }

        #region Overrides of RepositoryBase<int,ITemplate>

        protected override ITemplate PerformGet(int id)
        {
            //use the underlying GetAll which will force cache all templates
            return base.GetMany().FirstOrDefault(x => x.Id == id);
        }

        protected override IEnumerable<ITemplate> PerformGetAll(params int[] ids)
        {
            var sql = GetBaseQuery(false);

            if (ids.Any())
            {
                sql.Where("umbracoNode.id in (@ids)", new { ids = ids });
            }
            else
            {
                sql.Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId);
            }

            var dtos = Database.Fetch<TemplateDto>(sql);

            if (dtos.Count == 0) return Enumerable.Empty<ITemplate>();

            //look up the simple template definitions that have a master template assigned, this is used
            // later to populate the template item's properties
            var childIds = (ids.Any()
                ? GetAxisDefinitions(dtos.ToArray())
                : dtos.Select(x => new EntitySlim
                {
                    Id = x.NodeId,
                    ParentId = x.NodeDto.ParentId,
                    Name = x.Alias
                })).ToArray();

            return dtos.Select(d => MapFromDto(d, childIds));
        }

        protected override IEnumerable<ITemplate> PerformGetByQuery(IQuery<ITemplate> query)
        {
            var sqlClause = GetBaseQuery(false);
            var translator = new SqlTranslator<ITemplate>(sqlClause, query);
            var sql = translator.Translate();

            var dtos = Database.Fetch<TemplateDto>(sql);

            if (dtos.Count == 0) return Enumerable.Empty<ITemplate>();

            //look up the simple template definitions that have a master template assigned, this is used
            // later to populate the template item's properties
            var childIds = GetAxisDefinitions(dtos.ToArray()).ToArray();

            return dtos.Select(d => MapFromDto(d, childIds));
        }

        #endregion

        #region Overrides of NPocoRepositoryBase<int,ITemplate>

        protected override Sql<ISqlContext> GetBaseQuery(bool isCount)
        {
            var sql = SqlContext.Sql();

            sql = isCount
                ? sql.SelectCount()
                : sql.Select<TemplateDto>(r => r.Select(x => x.NodeDto));

            sql
                .From<TemplateDto>()
                .InnerJoin<NodeDto>()
                .On<TemplateDto, NodeDto>(left => left.NodeId, right => right.NodeId)
                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId);

            return sql;
        }

        protected override string GetBaseWhereClause()
        {
            return Constants.DatabaseSchema.Tables.Node + ".id = @id";
        }

        protected override IEnumerable<string> GetDeleteClauses()
        {
            var list = new List<string>
            {
                "DELETE FROM " + Constants.DatabaseSchema.Tables.User2NodeNotify + " WHERE nodeId = @id",
                "DELETE FROM " + Constants.DatabaseSchema.Tables.UserGroup2NodePermission + " WHERE nodeId = @id",
                "UPDATE " + Constants.DatabaseSchema.Tables.DocumentVersion + " SET templateId = NULL WHERE templateId = @id",
                "DELETE FROM " + Constants.DatabaseSchema.Tables.DocumentType + " WHERE templateNodeId = @id",
                "DELETE FROM " + Constants.DatabaseSchema.Tables.Template + " WHERE nodeId = @id",
                "DELETE FROM " + Constants.DatabaseSchema.Tables.Node + " WHERE id = @id"
            };
            return list;
        }

        protected override Guid NodeObjectTypeId => Constants.ObjectTypes.Template;

        protected override void PersistNewItem(ITemplate entity)
        {
            EnsureValidAlias(entity);

            //Save to db
            var template = (Template)entity;
            template.AddingEntity();

            var factory = new TemplateFactory(NodeObjectTypeId);
            var dto = factory.BuildDto(template);

            //Create the (base) node data - umbracoNode
            var nodeDto = dto.NodeDto;
            nodeDto.Path = "-1," + dto.NodeDto.NodeId;
            var o = Database.IsNew<NodeDto>(nodeDto) ? Convert.ToInt32(Database.Insert(nodeDto)) : Database.Update(nodeDto);

            //Update with new correct path
            var parent = Get(template.MasterTemplateId.Value);
            if (parent != null)
            {
                nodeDto.Path = string.Concat(parent.Path, ",", nodeDto.NodeId);
            }
            else
            {
                nodeDto.Path = "-1," + dto.NodeDto.NodeId;
            }
            Database.Update(nodeDto);

            //Insert template dto
            dto.NodeId = nodeDto.NodeId;
            Database.Insert(dto);

            //Update entity with correct values
            template.Id = nodeDto.NodeId; //Set Id on entity to ensure an Id is set
            template.Path = nodeDto.Path;

            //now do the file work
            SaveFile(template, dto);

            template.ResetDirtyProperties();

            // ensure that from now on, content is lazy-loaded
            if (template.GetFileContent == null)
                template.GetFileContent = file => GetFileContent((Template) file, false);
        }

        protected override void PersistUpdatedItem(ITemplate entity)
        {
            EnsureValidAlias(entity);

            //store the changed alias if there is one for use with updating files later
            var originalAlias = entity.Alias;
            if (entity.IsPropertyDirty("Alias"))
            {
                //we need to check what it currently is before saving and remove that file
                var current = Get(entity.Id);
                originalAlias = current.Alias;
            }

            var template = (Template)entity;

            if (entity.IsPropertyDirty("MasterTemplateId"))
            {
                var parent = Get(template.MasterTemplateId.Value);
                if (parent != null)
                {
                    entity.Path = string.Concat(parent.Path, ",", entity.Id);
                }
                else
                {
                    //this means that the master template has been removed, so we need to reset the template's
                    //path to be at the root
                    entity.Path = string.Concat("-1,", entity.Id);
                }
            }

            //Get TemplateDto from db to get the Primary key of the entity
            var templateDto = Database.SingleOrDefault<TemplateDto>("WHERE nodeId = @Id", new { Id = entity.Id });
            //Save updated entity to db

            template.UpdateDate = DateTime.Now;
            var factory = new TemplateFactory(templateDto.PrimaryKey, NodeObjectTypeId);
            var dto = factory.BuildDto(template);

            Database.Update(dto.NodeDto);
            Database.Update(dto);

            //re-update if this is a master template, since it could have changed!
            var axisDefs = GetAxisDefinitions(dto);
            template.IsMasterTemplate = axisDefs.Any(x => x.ParentId == dto.NodeId);

            //now do the file work
            SaveFile((Template) entity, dto, originalAlias);

            entity.ResetDirtyProperties();

            // ensure that from now on, content is lazy-loaded
            if (template.GetFileContent == null)
                template.GetFileContent = file => GetFileContent((Template) file, false);
        }

        private void SaveFile(Template template, TemplateDto dto, string originalAlias = null)
        {
            string content;

            var templateOnDisk = template as TemplateOnDisk;
            if (templateOnDisk != null && templateOnDisk.IsOnDisk)
            {
                // if "template on disk" load content from disk
                content = _viewHelper.GetFileContents(template);
            }
            else
            {
                // else, create or write template.Content to disk
                if (DetermineTemplateRenderingEngine(template) == RenderingEngine.Mvc)
                {
                    content = originalAlias == null
                        ? _viewHelper.CreateView(template, true)
                        : _viewHelper.UpdateViewFile(template, originalAlias);
                }
                else
                {
                    content = originalAlias == null
                        ? _masterPageHelper.CreateMasterPage(template, this, true)
                        : _masterPageHelper.UpdateMasterPageFile(template, originalAlias, this);
                }
            }

            // once content has been set, "template on disk" are not "on disk" anymore
            template.Content = content;
            SetVirtualPath(template);

            if (dto.Design == content) return;
            dto.Design = content;
            Database.Update(dto); // though... we don't care about the db value really??!!
        }

        protected override void PersistDeletedItem(ITemplate entity)
        {
            var deletes = GetDeleteClauses().ToArray();

            var descendants = GetDescendants(entity.Id).ToList();

            //change the order so it goes bottom up! (deepest level first)
            descendants.Reverse();

            //delete the hierarchy
            foreach (var descendant in descendants)
            {
                foreach (var delete in deletes)
                {
                    Database.Execute(delete, new { id = GetEntityId(descendant) });
                }
            }

            //now we can delete this one
            foreach (var delete in deletes)
            {
                Database.Execute(delete, new { id = GetEntityId(entity) });
            }

            if (DetermineTemplateRenderingEngine(entity) == RenderingEngine.Mvc)
            {
                var viewName = string.Concat(entity.Alias, ".cshtml");
                _viewsFileSystem.DeleteFile(viewName);
            }
            else
            {
                var masterpageName = string.Concat(entity.Alias, ".master");
                _masterpagesFileSystem.DeleteFile(masterpageName);
            }

            entity.DeleteDate = DateTime.Now;
        }

        #endregion

        private IEnumerable<IUmbracoEntity> GetAxisDefinitions(params TemplateDto[] templates)
        {
            //look up the simple template definitions that have a master template assigned, this is used
            // later to populate the template item's properties
            var childIdsSql = SqlContext.Sql()
                .Select("nodeId,alias,parentID")
                .From<TemplateDto>()
                .InnerJoin<NodeDto>()
                .On<TemplateDto, NodeDto>(dto => dto.NodeId, dto => dto.NodeId)
                //lookup axis's
                .Where("umbracoNode." + SqlContext.SqlSyntax.GetQuotedColumnName("id") + " IN (@parentIds) OR umbracoNode.parentID IN (@childIds)",
                    new {parentIds = templates.Select(x => x.NodeDto.ParentId), childIds = templates.Select(x => x.NodeId)});

            var childIds = Database.Fetch<dynamic>(childIdsSql)
                .Select(x => new EntitySlim
                {
                    Id = x.nodeId,
                    ParentId = x.parentID,
                    Name = x.alias
                });
            return childIds;
        }

        /// <summary>
        /// Maps from a dto to an ITemplate
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="axisDefinitions">
        /// This is a collection of template definitions ... either all templates, or the collection of child templates and it's parent template
        /// </param>
        /// <returns></returns>
        private ITemplate MapFromDto(TemplateDto dto, IUmbracoEntity[] axisDefinitions)
        {
            var factory = new TemplateFactory();
            var template = factory.BuildEntity(dto, axisDefinitions, file => GetFileContent((Template) file, false));

            if (dto.NodeDto.ParentId > 0)
            {
                var masterTemplate = axisDefinitions.FirstOrDefault(x => x.Id == dto.NodeDto.ParentId);
                if (masterTemplate != null)
                {
                    template.MasterTemplateAlias = masterTemplate.Name;
                    template.MasterTemplateId = new Lazy<int>(() => dto.NodeDto.ParentId);
                }
            }

            // get the infos (update date and virtual path) that will change only if
            // path changes - but do not get content, will get loaded only when required
            GetFileContent(template, true);

            // reset dirty initial properties (U4-1946)
            template.ResetDirtyProperties(false);

            return template;
        }

        private void SetVirtualPath(ITemplate template)
        {
            var path = template.OriginalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                // we need to discover the path
                path = string.Concat(template.Alias, ".cshtml");
                if (_viewsFileSystem.FileExists(path))
                {
                    template.VirtualPath = _viewsFileSystem.GetUrl(path);
                    return;
                }
                path = string.Concat(template.Alias, ".vbhtml");
                if (_viewsFileSystem.FileExists(path))
                {
                    template.VirtualPath = _viewsFileSystem.GetUrl(path);
                    return;
                }
                path = string.Concat(template.Alias, ".master");
                if (_masterpagesFileSystem.FileExists(path))
                {
                    template.VirtualPath = _masterpagesFileSystem.GetUrl(path);
                    return;
                }
            }
            else
            {
                // we know the path already
                var ext = Path.GetExtension(path);
                switch (ext)
                {
                    case ".cshtml":
                    case ".vbhtml":
                        template.VirtualPath = _viewsFileSystem.GetUrl(path);
                        return;
                    case ".master":
                        template.VirtualPath = _masterpagesFileSystem.GetUrl(path);
                        return;
                }
            }

            template.VirtualPath = string.Empty; // file not found...
        }

        private string GetFileContent(ITemplate template, bool init)
        {
            var path = template.OriginalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                // we need to discover the path
                path = string.Concat(template.Alias, ".cshtml");
                if (_viewsFileSystem.FileExists(path))
                    return GetFileContent(template, _viewsFileSystem, path, init);
                path = string.Concat(template.Alias, ".vbhtml");
                if (_viewsFileSystem.FileExists(path))
                    return GetFileContent(template, _viewsFileSystem, path, init);
                path = string.Concat(template.Alias, ".master");
                if (_masterpagesFileSystem.FileExists(path))
                    return GetFileContent(template, _masterpagesFileSystem, path, init);
            }
            else
            {
                // we know the path already
                var ext = Path.GetExtension(path);
                switch (ext)
                {
                    case ".cshtml":
                    case ".vbhtml":
                        return GetFileContent(template, _viewsFileSystem, path, init);
                    case ".master":
                        return GetFileContent(template, _masterpagesFileSystem, path, init);
                }
            }

            template.VirtualPath = string.Empty; // file not found...
            return string.Empty;
        }

        private string GetFileContent(ITemplate template, IFileSystem fs, string filename, bool init)
        {
            // do not update .UpdateDate as that would make it dirty (side-effect)
            // unless initializing, because we have to do it once
            if (init)
            {
                template.UpdateDate = fs.GetLastModified(filename).UtcDateTime;
            }

            // TODO
            //  see if this could enable us to update UpdateDate without messing with change tracking
            //  and then we'd want to do it for scripts, stylesheets and partial views too (ie files)
            //var xtemplate = template as Template;
            //xtemplate.DisableChangeTracking();
            //template.UpdateDate = fs.GetLastModified(filename).UtcDateTime;
            //xtemplate.EnableChangeTracking();

            template.VirtualPath = fs.GetUrl(filename);

            return init ? null : GetFileContent(fs, filename);
        }

        private string GetFileContent(IFileSystem fs, string filename)
        {
            using (var stream = fs.OpenFile(filename))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        public Stream GetFileContentStream(string filepath)
        {
            var fs = GetFileSystem(filepath);
            if (fs.FileExists(filepath) == false) return null;

            try
            {
                return GetFileSystem(filepath).OpenFile(filepath);
            }
            catch
            {
                return null; // deal with race conds
            }
        }

        public void SetFileContent(string filepath, Stream content)
        {
            GetFileSystem(filepath).AddFile(filepath, content, true);
        }

        public long GetFileSize(string filepath)
        {
            return GetFileSystem(filepath).GetSize(filepath);
        }

        private IFileSystem GetFileSystem(string filepath)
        {
            var ext = Path.GetExtension(filepath);
            IFileSystem fs;
            switch (ext)
            {
                case ".cshtml":
                case ".vbhtml":
                    fs = _viewsFileSystem;
                    break;
                case ".master":
                    fs = _masterpagesFileSystem;
                    break;
                default:
                    throw new Exception("Unsupported extension " + ext + ".");
            }
            return fs;
        }

        #region Implementation of ITemplateRepository

        public ITemplate Get(string alias)
        {
            return GetAll(alias).FirstOrDefault();
        }

        public IEnumerable<ITemplate> GetAll(params string[] aliases)
        {
            //We must call the base (normal) GetAll method
            // which is cached. This is a specialized method and unfortunatley with the params[] it
            // overlaps with the normal GetAll method.
            if (aliases.Any() == false) return base.GetMany();

            //return from base.GetAll, this is all cached
            return base.GetMany().Where(x => aliases.InvariantContains(x.Alias));
        }

        public IEnumerable<ITemplate> GetChildren(int masterTemplateId)
        {
            //return from base.GetAll, this is all cached
            var all = base.GetMany().ToArray();

            if (masterTemplateId <= 0) return all.Where(x => x.MasterTemplateAlias.IsNullOrWhiteSpace());

            var parent = all.FirstOrDefault(x => x.Id == masterTemplateId);
            if (parent == null) return Enumerable.Empty<ITemplate>();

            var children = all.Where(x => x.MasterTemplateAlias.InvariantEquals(parent.Alias));
            return children;
        }

        public IEnumerable<ITemplate> GetChildren(string alias)
        {
            //return from base.GetAll, this is all cached
            return base.GetMany().Where(x => alias.IsNullOrWhiteSpace()
                ? x.MasterTemplateAlias.IsNullOrWhiteSpace()
                : x.MasterTemplateAlias.InvariantEquals(alias));
        }

        public IEnumerable<ITemplate> GetDescendants(int masterTemplateId)
        {
            //return from base.GetAll, this is all cached
            var all = base.GetMany().ToArray();
            var descendants = new List<ITemplate>();
            if (masterTemplateId > 0)
            {
                var parent = all.FirstOrDefault(x => x.Id == masterTemplateId);
                if (parent == null) return Enumerable.Empty<ITemplate>();
                //recursively add all children with a level
                AddChildren(all, descendants, parent.Alias);
            }
            else
            {
                descendants.AddRange(all.Where(x => x.MasterTemplateAlias.IsNullOrWhiteSpace()));
                foreach (var parent in descendants)
                {
                    //recursively add all children with a level
                    AddChildren(all, descendants, parent.Alias);
                }
            }

            //return the list - it will be naturally ordered by level
            return descendants;
        }

        public IEnumerable<ITemplate> GetDescendants(string alias)
        {
            var all = base.GetMany().ToArray();
            var descendants = new List<ITemplate>();
            if (alias.IsNullOrWhiteSpace() == false)
            {
                var parent = all.FirstOrDefault(x => x.Alias.InvariantEquals(alias));
                if (parent == null) return Enumerable.Empty<ITemplate>();
                //recursively add all children
                AddChildren(all, descendants, parent.Alias);
            }
            else
            {
                descendants.AddRange(all.Where(x => x.MasterTemplateAlias.IsNullOrWhiteSpace()));
                foreach (var parent in descendants)
                {
                    //recursively add all children with a level
                    AddChildren(all, descendants, parent.Alias);
                }
            }
            //return the list - it will be naturally ordered by level
            return descendants;
        }

        private void AddChildren(ITemplate[] all, List<ITemplate> descendants, string masterAlias)
        {
            var c = all.Where(x => x.MasterTemplateAlias.InvariantEquals(masterAlias)).ToArray();
            descendants.AddRange(c);
            if (c.Any() == false) return;
            //recurse through all children
            foreach (var child in c)
            {
                AddChildren(all, descendants, child.Alias);
            }
        }

        /// <summary>
        /// Returns a template as a template node which can be traversed (parent, children)
        /// </summary>
        /// <param name="alias"></param>
        /// <returns></returns>
        [Obsolete("Use GetDescendants instead")]
        public TemplateNode GetTemplateNode(string alias)
        {
            //first get all template objects
            var allTemplates = base.GetMany().ToArray();

            var selfTemplate = allTemplates.SingleOrDefault(x => x.Alias.InvariantEquals(alias));
            if (selfTemplate == null)
            {
                return null;
            }

            var top = selfTemplate;
            while (top.MasterTemplateAlias.IsNullOrWhiteSpace() == false)
            {
                top = allTemplates.Single(x => x.Alias.InvariantEquals(top.MasterTemplateAlias));
            }

            var topNode = new TemplateNode(allTemplates.Single(x => x.Id == top.Id));
            var childTemplates = allTemplates.Where(x => x.MasterTemplateAlias.InvariantEquals(top.Alias));
            //This now creates the hierarchy recursively
            topNode.Children = CreateChildren(topNode, childTemplates, allTemplates);

            //now we'll return the TemplateNode requested
            return FindTemplateInTree(topNode, alias);
        }

        [Obsolete("Only used by obsolete code")]
        private static TemplateNode WalkTree(TemplateNode current, string alias)
        {
            //now walk the tree to find the node
            if (current.Template.Alias.InvariantEquals(alias))
            {
                return current;
            }
            foreach (var c in current.Children)
            {
                var found = WalkTree(c, alias);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Given a template node in a tree, this will find the template node with the given alias if it is found in the hierarchy, otherwise null
        /// </summary>
        /// <param name="anyNode"></param>
        /// <param name="alias"></param>
        /// <returns></returns>
        [Obsolete("Use GetDescendants instead")]
        public TemplateNode FindTemplateInTree(TemplateNode anyNode, string alias)
        {
            //first get the root
            var top = anyNode;
            while (top.Parent != null)
            {
                top = top.Parent;
            }
            return WalkTree(top, alias);
        }

        /// <summary>
        /// This checks what the default rendering engine is set in config but then also ensures that there isn't already
        /// a template that exists in the opposite rendering engine's template folder, then returns the appropriate
        /// rendering engine to use.
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// The reason this is required is because for example, if you have a master page file already existing under ~/masterpages/Blah.aspx
        /// and then you go to create a template in the tree called Blah and the default rendering engine is MVC, it will create a Blah.cshtml
        /// empty template in ~/Views. This means every page that is using Blah will go to MVC and render an empty page.
        /// This is mostly related to installing packages since packages install file templates to the file system and then create the
        /// templates in business logic. Without this, it could cause the wrong rendering engine to be used for a package.
        /// </remarks>
        public RenderingEngine DetermineTemplateRenderingEngine(ITemplate template)
        {
            var engine = _templateConfig.DefaultRenderingEngine;
            var viewHelper = new ViewHelper(_viewsFileSystem);
            if (viewHelper.ViewExists(template) == false)
            {
                if (template.Content.IsNullOrWhiteSpace() == false && MasterPageHelper.IsMasterPageSyntax(template.Content))
                {
                    //there is a design but its definitely a webforms design and we haven't got a MVC view already for it
                    return RenderingEngine.WebForms;
                }
            }

            var masterPageHelper = new MasterPageHelper(_masterpagesFileSystem);

            switch (engine)
            {
                case RenderingEngine.Mvc:
                    //check if there's a view in ~/masterpages
                    if (masterPageHelper.MasterPageExists(template) && viewHelper.ViewExists(template) == false)
                    {
                        //change this to webforms since there's already a file there for this template alias
                        engine = RenderingEngine.WebForms;
                    }
                    break;
                case RenderingEngine.WebForms:
                    //check if there's a view in ~/views
                    if (viewHelper.ViewExists(template) && masterPageHelper.MasterPageExists(template) == false)
                    {
                        //change this to mvc since there's already a file there for this template alias
                        engine = RenderingEngine.Mvc;
                    }
                    break;
            }
            return engine;
        }

        /// <summary>
        /// Validates a <see cref="ITemplate"/>
        /// </summary>
        /// <param name="template"><see cref="ITemplate"/> to validate</param>
        /// <returns>True if Script is valid, otherwise false</returns>
        public bool ValidateTemplate(ITemplate template)
        {
            // get path
            // TODO
            //  templates should have a real Path somehow - but anyways
            //  are we using Path for something else?!
            var path = template.VirtualPath;

            // get valid paths
            var validDirs = _templateConfig.DefaultRenderingEngine == RenderingEngine.Mvc
                ? new[] { SystemDirectories.Masterpages, SystemDirectories.MvcViews }
                : new[] { SystemDirectories.Masterpages };

            // get valid extensions
            var validExts = new List<string>();
            if (_templateConfig.DefaultRenderingEngine == RenderingEngine.Mvc)
            {
                validExts.Add("cshtml");
                validExts.Add("vbhtml");
            }
            else
            {
                validExts.Add("master");
            }

            // validate path and extension
            var validFile = IOHelper.VerifyEditPath(path, validDirs);
            var validExtension = IOHelper.VerifyFileExtension(path, validExts);
            return validFile && validExtension;
        }

        private static IEnumerable<TemplateNode> CreateChildren(TemplateNode parent, IEnumerable<ITemplate> childTemplates, ITemplate[] allTemplates)
        {
            var children = new List<TemplateNode>();
            foreach (var childTemplate in childTemplates)
            {
                var template = allTemplates.Single(x => x.Id == childTemplate.Id);
                var child = new TemplateNode(template)
                    {
                        Parent = parent
                    };

                //add to our list
                children.Add(child);

                //get this node's children
                var local = childTemplate;
                var kids = allTemplates.Where(x => x.MasterTemplateAlias.InvariantEquals(local.Alias));

                //recurse
                child.Children = CreateChildren(child, kids, allTemplates);
            }
            return children;
        }

        #endregion

        /// <summary>
        /// Ensures that there are not duplicate aliases and if so, changes it to be a numbered version and also verifies the length
        /// </summary>
        /// <param name="template"></param>
        private void EnsureValidAlias(ITemplate template)
        {
            //ensure unique alias
            template.Alias = template.Alias.ToCleanString(CleanStringType.UnderscoreAlias);

            if (template.Alias.Length > 100)
                template.Alias = template.Alias.Substring(0, 95);

            if (AliasAlreadExists(template))
            {
                template.Alias = EnsureUniqueAlias(template, 1);
            }
        }

        private bool AliasAlreadExists(ITemplate template)
        {
            var sql = GetBaseQuery(true).Where<TemplateDto>(x => x.Alias.InvariantEquals(template.Alias) && x.NodeId != template.Id);
            var count = Database.ExecuteScalar<int>(sql);
            return count > 0;
        }

        private string EnsureUniqueAlias(ITemplate template, int attempts)
        {
            //TODO: This is ported from the old data layer... pretty crap way of doing this but it works for now.
            if (AliasAlreadExists(template))
                return template.Alias + attempts;
            attempts++;
            return EnsureUniqueAlias(template, attempts);
        }
    }
}
