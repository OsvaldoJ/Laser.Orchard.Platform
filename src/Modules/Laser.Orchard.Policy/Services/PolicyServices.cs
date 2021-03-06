﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Laser.Orchard.Commons.Services;
using Laser.Orchard.Policy.Events;
using Laser.Orchard.Policy.Models;
using Laser.Orchard.Policy.ViewModels;
using Laser.Orchard.StartupConfig.Services;
using Newtonsoft.Json.Linq;
using Orchard;
using Orchard.ContentManagement;
using Orchard.Core.Title.Models;
using Orchard.Data;
using Orchard.Localization.Models;
using Orchard.Localization.Records;
using Orchard.Localization.Services;
using Orchard.Security;
using OrchardNS = Orchard;
using Orchard.Users.Models;

namespace Laser.Orchard.Policy.Services {
    public interface IPolicyServices : IDependency {
        /// <summary>
        /// Gets an object having a list of policies retrieved by culture, ordered by Priority (desc); if needed (writeMode==true) it saves the result into a cookie.
        /// </summary>
        /// <param name="writeMode">Tells if the policies should be stored into a cookie.</param>
        /// <param name="language">optional: the culture of the policies to get. if null the current culture is applied.</param>
        /// <returns>A PoliciesForUserViewModel object</returns>
        PoliciesForUserViewModel GetPoliciesForUserOrSession(bool writeMode, string language = null);
        void PolicyForItemUpdate(PolicyForUserViewModel viewModel, ContentItem item);
        void PolicyForUserUpdate(PolicyForUserViewModel viewModel, IUser user = null);
        void PolicyForItemMassiveUpdate(IList<PolicyForUserViewModel> viewModelCollection, ContentItem item);
        void PolicyForUserMassiveUpdate(IList<PolicyForUserViewModel> viewModelCollection, IUser user = null);
        IList<PolicyForUserViewModel> GetCookieOrVolatileAnswers();
        void CreateAndAttachPolicyCookie(IList<PolicyForUserViewModel> viewModelCollection, bool writeMode);
        string[] GetPoliciesForContent(PolicyPart part);
        /// <summary>
        /// Gets a list of policies by culture and/or ids, ordered by Priority (desc)
        /// </summary>
        /// <param name="culture">optional: the culture of the policies to get. if null the current culture is applied.</param>
        /// <param name="ids">optional: content ids of the policies to get. </param>
        /// <returns>A list of PolicyTextInfoPart</returns>
        IEnumerable<PolicyTextInfoPart> GetPolicies(string culture = null, int[] ids = null);
        List<PolicyHistoryViewModel> GetPolicyHistoryForUser(int userId);
        string PoliciesLMNVSerialization(IEnumerable<PolicyTextInfoPart> policies);
        string PoliciesPureJsonSerialization(IEnumerable<PolicyTextInfoPart> policies);
        IEnumerable<UserPolicyAnswersRecord> GetPolicyAnswersForContent(int contentId);
    }

    public class PolicyServices : IPolicyServices {
        private readonly IContentManager _contentManager;
        private readonly IContentSerializationServices _contentSerializationServices;
        private readonly OrchardNS.IWorkContextAccessor _workContext;
        private readonly ICultureManager _cultureManager;
        private readonly IRepository<UserPolicyAnswersRecord> _userPolicyAnswersRepository;
        private readonly IRepository<UserPolicyAnswersHistoryRecord> _userPolicyAnswersHistoryRepository;
        private readonly IControllerContextAccessor _controllerContextAccessor;
        private readonly IPolicyEventHandler _policyEventHandler;

        public PolicyServices(IContentManager contentManager,
                              IContentSerializationServices contentSerializationServices,
                              OrchardNS.IWorkContextAccessor workContext,
                              ICultureManager cultureManager,
                              IRepository<UserPolicyAnswersRecord> userPolicyAnswersRepository,
                              IRepository<UserPolicyAnswersHistoryRecord> userPolicyAnswersHistoryRepository,
                              IControllerContextAccessor controllerContextAccessor,
                              IPolicyEventHandler policyEventHandler) {
            _contentManager = contentManager;
            _contentSerializationServices = contentSerializationServices;
            _workContext = workContext;
            _cultureManager = cultureManager;
            _userPolicyAnswersRepository = userPolicyAnswersRepository;
            _userPolicyAnswersHistoryRepository = userPolicyAnswersHistoryRepository;
            _controllerContextAccessor = controllerContextAccessor;
            _policyEventHandler = policyEventHandler;
        }

        public PoliciesForUserViewModel GetPoliciesForUserOrSession(bool writeMode, string language = null) {
            var loggedUser = _workContext.GetContext().CurrentUser;
            var siteLanguage = _workContext.GetContext().CurrentSite.SiteCulture;

            int currentLanguageId;
            IList<PolicyForUserViewModel> model = new List<PolicyForUserViewModel>();
            IContentQuery<PolicyTextInfoPart> query;

            // language may be a string that does not represent any language. We should handle that case.
            CultureRecord currentLanguageRecord = null;
            if (!string.IsNullOrWhiteSpace(language)) {
                currentLanguageRecord = _cultureManager.GetCultureByName(language);
            }
            // if the language string is not a valid language (or it's empty):
            if (currentLanguageRecord == null) {
                currentLanguageRecord = _cultureManager.GetCultureByName(_workContext.GetContext().CurrentCulture);
            }
            if (currentLanguageRecord == null) {
                currentLanguageRecord = _cultureManager.GetCultureByName(_cultureManager.GetSiteCulture());
            }
            currentLanguageId = currentLanguageRecord.Id;

            query = _contentManager.Query<PolicyTextInfoPart, PolicyTextInfoPartRecord>()
                                   .OrderByDescending(o => o.Priority)
                                   .Join<LocalizationPartRecord>()
                                   .Where(w => w.CultureId == currentLanguageId || (w.CultureId == 0 && (siteLanguage.Equals(language) || language == null)))
                                   .ForVersion(VersionOptions.Published);

            if (loggedUser != null) { // loggato
                model = query.List().Select(s => {
                    var answer = loggedUser.As<UserPolicyPart>().UserPolicyAnswers.Where(w => w.PolicyTextInfoPartRecord.Id.Equals(s.Id)).SingleOrDefault();
                    return new PolicyForUserViewModel {
                        PolicyText = s,
                        PolicyTextId = s.Id,
                        AnswerId = answer != null ? answer.Id : 0,
                        AnswerDate = answer != null ? answer.AnswerDate : DateTime.MinValue,
                        OldAccepted = answer != null ? answer.Accepted : false,
                        Accepted = answer != null ? answer.Accepted : false,
                        UserId = (answer != null && answer.UserPartRecord != null) ? (int?)answer.UserPartRecord.Id : null
                    };
                }).ToList();
            }
            else { // non loggato
                IList<PolicyForUserViewModel> answers = GetCookieOrVolatileAnswers();
                model = query.List().Select(s => {
                    var answer = answers.Where(w => w.PolicyTextId.Equals(s.Id)).SingleOrDefault();
                    return new PolicyForUserViewModel {
                        PolicyText = s,
                        PolicyTextId = s.Id,
                        AnswerId = answer != null ? answer.AnswerId : 0,
                        AnswerDate = answer != null ? answer.AnswerDate : DateTime.MinValue,
                        OldAccepted = answer != null ? answer.Accepted : false,
                        Accepted = answer != null ? answer.Accepted : false,
                        UserId = answer != null ? answer.UserId : null
                    };
                }).ToList();
            }

            CreateAndAttachPolicyCookie(model.ToList(), writeMode);

            return new PoliciesForUserViewModel { Policies = model };
        }

        public void PolicyForUserUpdate(PolicyForUserViewModel viewModel, IUser user = null) {
            var loggedUser = user ?? _workContext.GetContext().CurrentUser;
            PolicyForItemUpdate(viewModel, loggedUser.ContentItem);
        }

        public void PolicyForItemUpdate(PolicyForUserViewModel viewModel, ContentItem item) {
            UserPolicyAnswersRecord record = null;
            var currentUser = _workContext.GetContext().CurrentUser;
            UserPartRecord currentUserPartRecord = null;
            if (currentUser != null) {
                currentUserPartRecord = currentUser.ContentItem.As<UserPart>().Record;
            }
            else if (item.ContentType == "User") {
                // in fase di registrazione di un nuovo utente
                currentUserPartRecord = item.As<UserPart>().Record;
            }

            // Recupero la risposta precedente, se esiste
            if (viewModel.AnswerId > 0)
                record = _userPolicyAnswersRepository.Get(viewModel.AnswerId);
            else
                record = _userPolicyAnswersRepository.Table.Where(w => w.PolicyTextInfoPartRecord.Id == viewModel.PolicyTextId && w.UserPolicyPartRecord.Id == item.Id).SingleOrDefault();

            bool oldAnswer = record != null ? record.Accepted : false;

            // Entro nella funzione solo se il valore della nuova risposta è diverso da quello della precedente o se si tratta della prima risposta
            if ((oldAnswer != viewModel.Accepted) || (record == null)) {
                var policyText = _contentManager.Get<PolicyTextInfoPart>(viewModel.PolicyTextId).Record;
                if ((policyText.UserHaveToAccept && viewModel.Accepted) || !policyText.UserHaveToAccept) {
                    var shouldCreateRecord = false;
                    if (item != null) {
                        if (viewModel.AnswerId <= 0 && record == null) {
                            record = new UserPolicyAnswersRecord();
                            shouldCreateRecord = true;
                        }
                        UserPolicyAnswersHistoryRecord recordForHistory = CopyForHistory(record);

                        //date and user should be updated only if it is a new record, or the answer has actually changed
                        record.AnswerDate = (shouldCreateRecord || oldAnswer != viewModel.Accepted) ? DateTime.UtcNow : record.AnswerDate;
                        if (shouldCreateRecord || oldAnswer != viewModel.Accepted) {
                            if (currentUserPartRecord == null && viewModel.UserId.HasValue) {
                                // utilizza il valore del viewModel
                                var userPart = _contentManager.Get<UserPart>(viewModel.UserId.Value);
                                currentUserPartRecord = (userPart == null) ? null : userPart.Record;
                            }
                            record.UserPartRecord = currentUserPartRecord;
                        }
                        record.Accepted = viewModel.Accepted;
                        record.UserPolicyPartRecord = item.As<UserPolicyPart>().Record;
                        record.PolicyTextInfoPartRecord = policyText;
                        if (shouldCreateRecord) {
                            _userPolicyAnswersRepository.Create(record);
                            _policyEventHandler.PolicyChanged(new PolicyEventViewModel {
                                policyType = record.PolicyTextInfoPartRecord.PolicyType,
                                accepted = record.Accepted,
                                ItemPolicyPartRecordId = item.Id
                            });
                        }
                        else if (record.Accepted != recordForHistory.Accepted) {
                            _userPolicyAnswersHistoryRepository.Create(recordForHistory);
                            _userPolicyAnswersRepository.Update(record);
                            _policyEventHandler.PolicyChanged(new PolicyEventViewModel {
                                policyType = record.PolicyTextInfoPartRecord.PolicyType,
                                accepted = record.Accepted,
                                ItemPolicyPartRecordId = item.Id
                            });
                        }
                    }
                }
                else if (policyText.UserHaveToAccept && !viewModel.Accepted && record != null) {
                    UserPolicyAnswersHistoryRecord recordForHistory = CopyForHistory(record);
                    _userPolicyAnswersHistoryRepository.Create(recordForHistory);
                    _userPolicyAnswersRepository.Delete(record);
                    _policyEventHandler.PolicyChanged(new PolicyEventViewModel {
                        policyType = recordForHistory.PolicyTextInfoPartRecord.PolicyType,
                        accepted = false,
                        ItemPolicyPartRecordId = item.Id
                    });
                }
            }
        }
        public void PolicyForUserMassiveUpdate(IList<PolicyForUserViewModel> viewModelCollection, IUser user = null) {
            var loggedUser = user ?? _workContext.GetContext().CurrentUser;
            if (loggedUser != null) {
                foreach (var item in viewModelCollection) {
                    PolicyForUserUpdate(item, loggedUser);
                }
            }
            //Dopo aver salvatao gli eventuali record, aggiorno anche il campo AnswerDate per il cookie. Devo farlo assolutamente dopo il salvataggio in quanto è l'unico modo per stabilire se si tratta di prima risposta o meno.
            CreateAndAttachPolicyCookie(viewModelCollection, true);
        }

        public void PolicyForItemMassiveUpdate(IList<PolicyForUserViewModel> viewModelCollection, ContentItem item) {
            if (item != null) {
                foreach (var policy in viewModelCollection) {
                    PolicyForItemUpdate(policy, item);
                }
            }
        }

        public IList<PolicyForUserViewModel> GetCookieOrVolatileAnswers() {
            var viewModelCollection = _controllerContextAccessor.Context != null ? _controllerContextAccessor.Context.Controller.ViewBag.PoliciesAnswers : null;
            IList<PolicyForUserViewModel> answers;
            try {
                if (viewModelCollection == null) {
                    answers = GetCookieAnswers();
                }
                else {
                    answers = (IList<PolicyForUserViewModel>)viewModelCollection;
                }
            }
            catch {
                answers = new List<PolicyForUserViewModel>();
            }
            return answers;
        }

        public void CreateAndAttachPolicyCookie(IList<PolicyForUserViewModel> viewModelCollection, bool writeMode) {

            var newCollection = new List<PolicyForUserViewModel>();

            //Controllo se esistono già delle policy answers nel cookie
            IList<PolicyForUserViewModel> previousPolicyAnswers = GetCookieAnswers();
            if (previousPolicyAnswers.Count > 0) {
                foreach (PolicyForUserViewModel policyAnswer in previousPolicyAnswers) {
                    var upToDateAnswer = viewModelCollection.Where(x => x.PolicyTextId == policyAnswer.PolicyTextId).SingleOrDefault();
                    if (upToDateAnswer == null) {
                        newCollection.Add(policyAnswer); // Se la risposta nel cookie non ha un corrispettivo nel json la aggiungo sempre al nuovo cookie
                    }
                    else if (upToDateAnswer.Accepted == policyAnswer.Accepted) {
                        newCollection.Add(policyAnswer); // Se si ripete con lo stesso esito riporto quella vecchia in modo da mantenere la data di accettazione
                        viewModelCollection.Remove(upToDateAnswer);
                    }
                }
            }

            if (writeMode) {
                newCollection.AddRange(viewModelCollection.Select(x => {
                    x.AnswerDate = DateTime.UtcNow;
                    //x.PolicyText = null; // annullo la parte per evitare circolarità nella serializzazione
                    return x;
                }));
            }

            string myObjectJson = new JavaScriptSerializer().Serialize(newCollection.Where(w => {
                var policyText = _contentManager.Get<PolicyTextInfoPart>(w.PolicyTextId);
                if (policyText == null) return false;
                else {
                    var policyTextRecord = policyText.Record;
                    return (policyTextRecord.UserHaveToAccept && w.Accepted) || !policyTextRecord.UserHaveToAccept;
                }
            }));

            var cookie = new HttpCookie("PoliciesAnswers", Convert.ToBase64String(Encoding.UTF8.GetBytes(myObjectJson))) { // cookie salvato in base64
                Expires = DateTime.Now.AddMonths(6)
            };
            if (_controllerContextAccessor.Context != null)
                _controllerContextAccessor.Context.Controller.ViewBag.PoliciesAnswers = viewModelCollection;
            if (_workContext.GetContext().HttpContext.Response.Cookies["PoliciesAnswers"] != null) {
                _workContext.GetContext().HttpContext.Response.Cookies.Set(cookie);
            }
            else {
                _workContext.GetContext().HttpContext.Response.Cookies.Add(cookie);
            }

        }

        public string[] GetPoliciesForContent(PolicyPart part) {
            var settings = part.Settings.GetModel<PolicyPartSettings>();

            if (!settings.PolicyTextReferences.Contains("{DependsOnContent}"))
                return settings.PolicyTextReferences;
            else if (!part.PolicyTextReferences.Contains("{All}"))
                return part.PolicyTextReferences;
            else
                return null;
        }

        public IEnumerable<PolicyTextInfoPart> GetPolicies(string culture = null, int[] ids = null) {
            var siteLanguage = _workContext.GetContext().CurrentSite.SiteCulture;

            int currentLanguageId;
            IList<PolicyForUserViewModel> model = new List<PolicyForUserViewModel>();
            IContentQuery<PolicyTextInfoPart> query;
            CultureRecord cultureRecord = null;
            if (!String.IsNullOrWhiteSpace(culture)) {
                cultureRecord = _cultureManager.GetCultureByName(culture);
            }
            if (cultureRecord == null) {
                //Nel caso di contenuto senza Localizationpart prendo la CurrentCulture
                cultureRecord = _cultureManager.GetCultureByName(_workContext.GetContext().CurrentCulture);
            }
            if (cultureRecord == null) {
                cultureRecord = _cultureManager.GetCultureByName(_cultureManager.GetSiteCulture());
            }
            currentLanguageId = cultureRecord.Id;

            if (ids != null) {
                query = _contentManager.Query<PolicyTextInfoPart, PolicyTextInfoPartRecord>()
                           .Where(x => ids.Contains(x.Id))
                           .OrderByDescending(o => o.Priority)
                           .Join<LocalizationPartRecord>()
                           .Where(w => w.CultureId == currentLanguageId || (w.CultureId == 0 && (siteLanguage.Equals(culture) || culture == null)))
                           .ForVersion(VersionOptions.Published);
            }
            else {
                query = _contentManager.Query<PolicyTextInfoPart, PolicyTextInfoPartRecord>()
                           .OrderByDescending(o => o.Priority)
                           .Join<LocalizationPartRecord>()
                           .Where(w => w.CultureId == currentLanguageId || (w.CultureId == 0 && (siteLanguage.Equals(culture) || culture == null)))
                           .ForVersion(VersionOptions.Published);
            }

            return query.List<PolicyTextInfoPart>();
        }

        public List<PolicyHistoryViewModel> GetPolicyHistoryForUser(int userId) {
            List<PolicyHistoryViewModel> policyHistory = new List<PolicyHistoryViewModel>();
            var currentAnswers = _userPolicyAnswersRepository.Table.Where(w => w.UserPolicyPartRecord.Id == userId);
            var oldAnswers = _userPolicyAnswersHistoryRepository.Table.Where(w => w.UserPolicyPartRecord.Id == userId);

            policyHistory.AddRange(currentAnswers.ToList().Select(s => {
                var content = _contentManager.Get(s.PolicyTextInfoPartRecord.Id, VersionOptions.Latest);
                if (content != null)
                    return new PolicyHistoryViewModel {
                        PolicyId = s.PolicyTextInfoPartRecord.Id,
                        PolicyTitle = content.As<TitlePart>().Title,
                        PolicyType = s.PolicyTextInfoPartRecord.PolicyType,
                        Accepted = s.Accepted,
                        AnswerDate = s.AnswerDate,
                        EndValidity = null,
                        UserName = (s.UserPartRecord != null) ? s.UserPartRecord.UserName : ""
                    };
                else
                    return null;
            }).Where(w => w != null));

            policyHistory.AddRange(oldAnswers.ToList().Select(s => {
                var content = _contentManager.Get(s.PolicyTextInfoPartRecord.Id, VersionOptions.Latest);
                if (content != null)
                    return new PolicyHistoryViewModel {
                        PolicyId = s.PolicyTextInfoPartRecord.Id,
                        PolicyTitle = content.As<TitlePart>().Title,
                        PolicyType = s.PolicyTextInfoPartRecord.PolicyType,
                        Accepted = s.Accepted,
                        AnswerDate = s.AnswerDate,
                        EndValidity = s.EndValidity,
                        UserName = (s.UserPartRecord != null) ? s.UserPartRecord.UserName : ""
                    };
                else
                    return null;
            }).Where(w => w != null));

            return policyHistory.OrderBy(o => o.PolicyId).ThenBy(o => o.PolicyTitle).ThenBy(o => o.AnswerDate).ToList();
        }

        public string PoliciesLMNVSerialization(IEnumerable<PolicyTextInfoPart> policies) {
            ObjectDumper dumper;
            StringBuilder sb = new StringBuilder();
            XElement dump = null;

            var realFormat = false;
            var minified = false;
            string[] complexBehaviours = null;
            string outputFormat = _workContext.GetContext().HttpContext.Request.Headers["OutputFormat"];

            if (outputFormat != null && outputFormat.Equals("LMNV", StringComparison.OrdinalIgnoreCase)) {
                complexBehaviours = new string[] { "returnnulls" };
                realFormat = true;
                minified = false;
            }
            else {
                bool.TryParse(_workContext.GetContext().HttpContext.Request["realformat"], out realFormat);
                bool.TryParse(_workContext.GetContext().HttpContext.Request["minified"], out minified);
            }

            sb.Insert(0, "{");
            sb.AppendFormat("\"n\": \"{0}\"", "Model");
            sb.AppendFormat(", \"v\": \"{0}\"", "VirtualContent");
            sb.Append(", \"m\": [{");
            sb.AppendFormat("\"n\": \"{0}\"", "VirtualId");
            sb.AppendFormat(", \"v\": \"{0}\"", "0");
            sb.Append("}]");

            sb.Append(", \"l\":[");

            int i = 0;
            sb.Append("{");
            sb.AppendFormat("\"n\": \"{0}\"", "PendingPolicies");
            sb.AppendFormat(", \"v\": \"{0}\"", "ContentItem[]");
            sb.Append(", \"m\": [");

            foreach (var item in policies) {
                if (i > 0) {
                    sb.Append(",");
                }
                sb.Append("{");
                dumper = new ObjectDumper(10, null, false, true, complexBehaviours);
                dump = dumper.Dump(item.ContentItem, String.Format("[{0}]", i));
                JsonConverter.ConvertToJSon(dump, sb, minified, realFormat);
                sb.Append("}");
                i++;
            }
            sb.Append("]");
            sb.Append("}");

            sb.Append("]");
            sb.Append("}");

            return sb.ToString();
        }

        public string PoliciesPureJsonSerialization(IEnumerable<PolicyTextInfoPart> policies) {
            JObject json = new JObject();
            var resultArray = new JArray();

            foreach (var pendingPolicy in policies) {
                resultArray.Add(new JObject(_contentSerializationServices.SerializeContentItem(pendingPolicy.ContentItem, 0)));
            }

            json.Add("PendingPolicies", resultArray);

            return json.ToString(Newtonsoft.Json.Formatting.None);
        }

        private IList<PolicyForUserViewModel> GetCookieAnswers() {
            HttpCookie cookie = _workContext.GetContext().HttpContext.Request.Cookies["PoliciesAnswers"];
            if (cookie != null && cookie.Value != null)
                try {
                    return new JavaScriptSerializer().Deserialize<List<PolicyForUserViewModel>>(Encoding.UTF8.GetString(Convert.FromBase64String(cookie.Value)));
                }
                catch {
                    return new List<PolicyForUserViewModel>();
                }
            else
                return new List<PolicyForUserViewModel>();
        }

        private UserPolicyAnswersHistoryRecord CopyForHistory(UserPolicyAnswersRecord originalRecord) {
            UserPolicyAnswersHistoryRecord recordForHistory = new UserPolicyAnswersHistoryRecord();

            recordForHistory.Accepted = originalRecord.Accepted;
            recordForHistory.AnswerDate = originalRecord.AnswerDate;
            recordForHistory.EndValidity = DateTime.UtcNow.AddSeconds(-1);
            recordForHistory.PolicyTextInfoPartRecord = originalRecord.PolicyTextInfoPartRecord;
            recordForHistory.UserPolicyPartRecord = originalRecord.UserPolicyPartRecord;
            recordForHistory.UserPartRecord = originalRecord.UserPartRecord;
            return recordForHistory;
        }
        public IEnumerable<UserPolicyAnswersRecord> GetPolicyAnswersForContent(int contentId) {
            var answers = _userPolicyAnswersRepository.Fetch(x => x.UserPolicyPartRecord.Id == contentId);
            return answers.ToList();
        }
    }
}