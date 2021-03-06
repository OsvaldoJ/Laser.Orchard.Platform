﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using AutoMapper;
using Laser.Orchard.ContentExtension.Models;
using Laser.Orchard.ContentExtension.ViewModels;
using Orchard;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Drivers;
using Orchard.ContentManagement.Handlers;
using Orchard.Core.Title.Models;
using Orchard.Data;
using Orchard.DisplayManagement;
using Orchard.Environment.Configuration;
using Orchard.Environment.Extensions;
using Orchard.Forms.Services;
using Orchard.Localization;
using Orchard.Projections.Descriptors.Layout;
using Orchard.Projections.Descriptors.Property;
using Orchard.Projections.Models;
using Orchard.Projections.Services;
using Orchard.Projections.ViewModels;
using Orchard.Security;
using Orchard.Tokens;
using Orchard.UI.Navigation;
using Orchard.Utility.Extensions;

namespace Laser.Orchard.ContentExtension.Drivers {
    [OrchardFeature("Laser.Orchard.ContentExtension.DynamicProjection")]
    public class DynamicProjectionPartDriver : ContentPartDriver<DynamicProjectionPart> {

        private readonly IAuthorizationService _authorizationService;
        private readonly INavigationManager _navigationManager;
        private readonly IOrchardServices _orchardServices;
        private readonly IProjectionManagerExtension _projectionManager;
        private readonly IRepository<QueryPartRecord> _queryRepository;
        private readonly ITokenizer _tokenizer;
        private readonly IDisplayHelperFactory _displayHelperFactory;
        private readonly IWorkContextAccessor _workContextAccessor;
        private readonly ShellSettings _shellSettings;
        //  private readonly IFeedManager _feedManager;

        protected override string Prefix {
            get { return "Laser.DynamicProjectionPart"; }
        }
        public DynamicProjectionPartDriver(
            IAuthorizationService authorizationService, 
            INavigationManager navigationManager, 
            IOrchardServices orchardServices,
            IProjectionManagerExtension projectionManager, 
            IRepository<QueryPartRecord> queryRepository,
            ITokenizer tokenizer,
            IDisplayHelperFactory displayHelperFactory,
            IWorkContextAccessor workContextAccessor,
             ShellSettings shellSettings) {
            _authorizationService = authorizationService;
            _navigationManager = navigationManager;
            _orchardServices = orchardServices;
            _projectionManager = projectionManager;
            _queryRepository=queryRepository;
            T = NullLocalizer.Instance;
            _tokenizer = tokenizer;
            _displayHelperFactory = displayHelperFactory;
            _workContextAccessor = workContextAccessor;
            _shellSettings = shellSettings;
        }

        public Localizer T { get; set; }

        //private string GetDefaultPosition(ContentPart part) {
        //    var settings = part.Settings.GetModel<AdminMenuPartTypeSettings>();
        //    var defaultPosition = settings == null ? "" : settings.DefaultPosition;
        //    var adminMenu = _navigationManager.BuildMenu("admin");
        //    if (!string.IsNullOrEmpty(defaultPosition)) {
        //        int major;
        //        return int.TryParse(defaultPosition, out major) ? Position.GetNextMinor(major, adminMenu) : defaultPosition;
        //    }
        //    return Position.GetNext(adminMenu);
        //}
        private class ViewDataContainer : IViewDataContainer {
            public ViewDataDictionary ViewData { get; set; }
        }
        protected override DriverResult Display(DynamicProjectionPart part, string displayType, dynamic shapeHelper) {
            var query = part.Record.QueryPartRecord;

            // retrieving paging parameters
            var queryString = _orchardServices.WorkContext.HttpContext.Request.QueryString;

            var pageKey = String.IsNullOrWhiteSpace(part.Record.PagerSuffix) ? "page" : "page-" + part.Record.PagerSuffix;
            var page = 0;

            // default page size
            int pageSize = part.Record.Items;

            // don't try to page if not necessary
            if (part.Record.DisplayPager && queryString.AllKeys.Contains(pageKey)) {
                Int32.TryParse(queryString[pageKey], out page);
            }

            // if 0, then assume "All", limit to 128 by default
            if (pageSize == 128) {
                pageSize = Int32.MaxValue;
            }

            // if pageSize is provided on the query string, ensure it is compatible with allowed limits
            var pageSizeKey = "pageSize" + part.Record.PagerSuffix;

            if (queryString.AllKeys.Contains(pageSizeKey)) {
                int qsPageSize;

                if (Int32.TryParse(queryString[pageSizeKey], out qsPageSize)) {
                    if (part.Record.MaxItems == 0 || qsPageSize <= part.Record.MaxItems) {
                        pageSize = qsPageSize;
                    }
                }
            }

            var pager = new Pager(_orchardServices.WorkContext.CurrentSite, page, pageSize);

            var pagerShape = shapeHelper.Pager(pager)
                .ContentPart(part)
                .PagerId(pageKey);

            return Combined(
                ContentShape("Parts_ProjectionPart_Pager", shape => {
                    if (!part.Record.DisplayPager) {
                        return null;
                    }

                    return pagerShape;
                }),
                ContentShape("Parts_ProjectionPart_List", shape => {

                        // generates a link to the RSS feed for this term
                        var metaData = _orchardServices.ContentManager.GetItemMetadata(part.ContentItem);
                        //      _feedManager.Register(metaData.DisplayText, "rss", new RouteValueDictionary { { "projection", part.Id } });

                        // execute the query
                        var contentItems = _projectionManager.GetContentItems(query.Id, part, pager.GetStartIndex() + part.Record.Skip, pager.PageSize).ToList();

                        // sanity check so that content items with ProjectionPart can't be added here, or it will result in an infinite loop
                        contentItems = contentItems.Where(x => !x.Has<ProjectionPart>()).ToList();

                        // applying layout
                        var layout = part.Record.LayoutRecord;

                    LayoutDescriptor layoutDescriptor = layout == null ? null : _projectionManager.DescribeLayouts().SelectMany(x => x.Descriptors).FirstOrDefault(x => x.Category == layout.Category && x.Type == layout.Type);

                        // create pager shape
                        if (part.Record.DisplayPager) {
                        var contentItemsCount = _projectionManager.GetCount(query.Id, part) - part.Record.Skip;
                        contentItemsCount = Math.Max(0, contentItemsCount);
                        pagerShape.TotalItemCount(contentItemsCount);
                    }

                        // renders in a standard List shape if no specific layout could be found
                        if (layoutDescriptor == null) {
                        var list = _orchardServices.New.List();
                        var contentShapes = contentItems.Select(item => _orchardServices.ContentManager.BuildDisplay(item, "Summary"));
                        list.AddRange(contentShapes);

                        return list;
                    }

                    var allFielDescriptors = _projectionManager.DescribeProperties().ToList();
                    var fieldDescriptors = layout.Properties.OrderBy(p => p.Position).Select(p => allFielDescriptors.SelectMany(x => x.Descriptors).Select(d => new { Descriptor = d, Property = p }).FirstOrDefault(x => x.Descriptor.Category == p.Category && x.Descriptor.Type == p.Type)).ToList();

                    var layoutComponents = contentItems.Select(
                        contentItem => {

                            var contentItemMetadata = _orchardServices.ContentManager.GetItemMetadata(contentItem);

                            var propertyDescriptors = fieldDescriptors.Select(
                                d => {
                                    var fieldContext = new PropertyContext {
                                        State = FormParametersHelper.ToDynamic(d.Property.State),
                                        Tokens = new Dictionary<string, object> { { "Content", contentItem } }
                                    };

                                    return new { d.Property, Shape = d.Descriptor.Property(fieldContext, contentItem) };
                                });

                                // apply all settings to the field content, wrapping it in a FieldWrapper shape
                                var properties = _orchardServices.New.Properties(
                                Items: propertyDescriptors.Select(
                                    pd => _orchardServices.New.PropertyWrapper(
                                        Item: pd.Shape,
                                        Property: pd.Property,
                                        ContentItem: contentItem,
                                        ContentItemMetadata: contentItemMetadata
                                    )
                                )
                            );

                            return new LayoutComponentResult {
                                ContentItem = contentItem,
                                ContentItemMetadata = contentItemMetadata,
                                Properties = properties
                            };

                        }).ToList();

                    var tokenizedState = _tokenizer.Replace(layout.State, new Dictionary<string, object> { { "Content", part.ContentItem } });

                    var renderLayoutContext = new LayoutContext {
                        State = FormParametersHelper.ToDynamic(tokenizedState),
                        LayoutRecord = layout,
                    };

                    if (layout.GroupProperty != null) {
                        var groupPropertyId = layout.GroupProperty.Id;
                        var display = _displayHelperFactory.CreateHelper(new ViewContext { HttpContext = _workContextAccessor.GetContext().HttpContext }, new ViewDataContainer());

                            // index by PropertyWrapper shape
                            var groups = layoutComponents.GroupBy(
                            x => {
                                var propertyShape = ((IEnumerable<dynamic>)x.Properties.Items).First(p => ((PropertyRecord)p.Property).Id == groupPropertyId);

                                    // clear the wrappers, as they shouldn't be needed to generate the grouping key itself
                                    // otherwise the DisplayContext.View is null, and throws an exception if a wrapper is rendered (#18558)
                                    ((IShape)propertyShape).Metadata.Wrappers.Clear();

                                string key = Convert.ToString(display(propertyShape));
                                return key;
                            }).Select(x => new { Key = x.Key, Components = x });

                        var list = _orchardServices.New.List();
                        foreach (var group in groups) {

                            var localResult = layoutDescriptor.Render(renderLayoutContext, group.Components);
                                // add the Context to the shape
                                localResult.Context(renderLayoutContext);

                            list.Add(_orchardServices.New.LayoutGroup(Key: new MvcHtmlString(group.Key), List: localResult));
                        }

                        return list;
                    }


                    var layoutResult = layoutDescriptor.Render(renderLayoutContext, layoutComponents);

                        // add the Context to the shape
                        layoutResult.Context(renderLayoutContext);

                    return layoutResult;
                }));
        }

        private static string GetLayoutDescription(IEnumerable<LayoutDescriptor> layouts, LayoutRecord l) {
            var descriptor = layouts.FirstOrDefault(x => l.Category == x.Category && l.Type == x.Type);
            return String.IsNullOrWhiteSpace(l.Description) ? descriptor.Display(new LayoutContext { State = FormParametersHelper.ToDynamic(l.State) }).Text : l.Description;
        }

        protected override DriverResult Editor(DynamicProjectionPart part, dynamic shapeHelper) {
            // todo: we need a 'ManageAdminMenu' too?
            //if (!_authorizationService.TryCheckAccess(DynamicProjectionPermission.ManageContentMenus, _orchardServices.WorkContext.CurrentUser, part)) {
            //    return null;
            //}

            if (string.IsNullOrEmpty(part.AdminMenuPosition)) {
                part.AdminMenuPosition = "2"; // GetDefaultPosition(part);
            }
            var model = new ProjectionPartEditViewModel {
                DisplayPager = part.Record.DisplayPager,
                Items = part.Record.Items,
                ItemsPerPage = part.Record.ItemsPerPage,
                Skip = part.Record.Skip,
                PagerSuffix = part.Record.PagerSuffix,
                MaxItems = part.Record.MaxItems,
                QueryLayoutRecordId = "-1",
            };

            // concatenated Query and Layout ids for the view
            if (part.Record.QueryPartRecord != null) {
                model.QueryLayoutRecordId = part.Record.QueryPartRecord.Id + ";";
            }

            if (part.Record.LayoutRecord != null) {
                model.QueryLayoutRecordId += part.Record.LayoutRecord.Id.ToString();
            }
            else {
                model.QueryLayoutRecordId += "-1";
            }

            // populating the list of queries and layouts
            var layouts = _projectionManager.DescribeLayouts().SelectMany(x => x.Descriptors).ToList();
            model.QueryRecordEntries = _orchardServices.ContentManager.Query<QueryPart, QueryPartRecord>().Join<TitlePartRecord>().OrderBy(x => x.Title).List()
                .Select(x => new QueryRecordEntry {
                    Id = x.Id,
                    Name = x.Name,
                    LayoutRecordEntries = x.Layouts.Select(l => new LayoutRecordEntry {
                        Id = l.Id,
                        Description = GetLayoutDescription(layouts, l)
                    })
                });
            var partvm = new DynamicProjectionPartVM();
            Mapper.Initialize(cfg => {
                cfg.CreateMap<DynamicProjectionPart, DynamicProjectionPartVM>();
            });
            Mapper.Map<DynamicProjectionPart, DynamicProjectionPartVM>(part,partvm);
            var newmodel = new DynamicProjectionVM {
                Projection = model,
                Part = partvm,
              //  FormFile =part.FormFile,
                Tenant = _shellSettings.Name
            };
            return ContentShape("Parts_Navigation_DynamicProjection_Edit",
                        () => shapeHelper.EditorTemplate(TemplateName: "Parts.Navigation.DynamicProjection.Edit", Model: newmodel, Prefix: Prefix));
        }

        protected override DriverResult Editor(DynamicProjectionPart part, IUpdateModel updater, dynamic shapeHelper) {
            //if (!_authorizationService.TryCheckAccess(DynamicProjectionPermission.ManageContentMenus, _orchardServices.WorkContext.CurrentUser, part))
            //    return null;
            var newmodel = new DynamicProjectionVM();
            updater.TryUpdateModel(newmodel, Prefix, null, null);

            if (newmodel.Part.OnAdminMenu) {
                if (string.IsNullOrEmpty(newmodel.Part.AdminMenuText)) {
                    updater.AddModelError("AdminMenuText", T("The AdminMenuText field is required"));
                }

                if (string.IsNullOrEmpty(newmodel.Part.AdminMenuPosition)) {
                    newmodel.Part.AdminMenuPosition = "2";// GetDefaultPosition(part);
                }
            }
            else {
                newmodel.Part.AdminMenuPosition = "";
            }
            Mapper.Initialize(cfg => {
                cfg.CreateMap<DynamicProjectionPartVM, DynamicProjectionPart>();
            });
            Mapper.Map<DynamicProjectionPartVM, DynamicProjectionPart>(newmodel.Part,part);
//           part.FormFile = newmodel.FormFile;
            // projection
            var model = newmodel.Projection;

    

                var queryLayoutIds = model.QueryLayoutRecordId.Split(new[] { ';' });

                part.Record.DisplayPager = model.DisplayPager;
                part.Record.Items = model.Items;
                part.Record.ItemsPerPage = model.ItemsPerPage;
                part.Record.Skip = model.Skip;
                part.Record.MaxItems = model.MaxItems;
                part.Record.PagerSuffix = (model.PagerSuffix ?? String.Empty).Trim();
                part.Record.QueryPartRecord = _queryRepository.Get(Int32.Parse(queryLayoutIds[0]));
                part.Record.LayoutRecord = part.Record.QueryPartRecord.Layouts.FirstOrDefault(x => x.Id == Int32.Parse(queryLayoutIds[1]));

                if (!String.IsNullOrWhiteSpace(part.Record.PagerSuffix) && !String.Equals(part.Record.PagerSuffix.ToSafeName(), part.Record.PagerSuffix, StringComparison.OrdinalIgnoreCase)) {
                    updater.AddModelError("PagerSuffix", T("Suffix should not contain special characters."));
                }
        
//


            return Editor(part, shapeHelper);
        }

        protected override void Importing(DynamicProjectionPart part, ImportContentContext context) {
            // Don't do anything if the tag is not specified.
            if (context.Data.Element(part.PartDefinition.Name) == null) {
                return;
            }

            context.ImportAttribute(part.PartDefinition.Name, "AdminMenuText", adminMenuText =>
                part.AdminMenuText = adminMenuText
            );

            context.ImportAttribute(part.PartDefinition.Name, "AdminMenuPosition", position =>
                part.AdminMenuPosition = position
            );

            context.ImportAttribute(part.PartDefinition.Name, "OnAdminMenu", onAdminMenu =>
                part.OnAdminMenu = Convert.ToBoolean(onAdminMenu)
            );
        }

        protected override void Exporting(DynamicProjectionPart part, ExportContentContext context) {
            context.Element(part.PartDefinition.Name).SetAttributeValue("AdminMenuText", part.AdminMenuText);
            context.Element(part.PartDefinition.Name).SetAttributeValue("AdminMenuPosition", part.AdminMenuPosition);
            context.Element(part.PartDefinition.Name).SetAttributeValue("OnAdminMenu", part.OnAdminMenu);
        }
    }
}