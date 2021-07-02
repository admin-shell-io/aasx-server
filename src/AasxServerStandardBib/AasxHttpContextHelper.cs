using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Permissions;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AasxServer;
using AdminShellNS;
using Grapevine.Interfaces.Server;
using Grapevine.Server;
using Grapevine.Server.Attributes;
using Grapevine.Shared;
using Jose;
using Jose.jwe;
using Jose.netstandard1_4;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

/* Copyright (c) 2018-2019 Festo AG & Co. KG <https://www.festo.com/net/de_de/Forms/web/contact_international>, author: Michael Hoffmeister
This software is licensed under the Eclipse Public License 2.0 (EPL-2.0) (see https://www.eclipse.org/org/documents/epl-2.0/EPL-2.0.txt).
The browser functionality is under the cefSharp license (see https://raw.githubusercontent.com/cefsharp/CefSharp/master/LICENSE).
The JSON serialization is under the MIT license (see https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md).
The QR code generation is under the MIT license (see https://github.com/codebude/QRCoder/blob/master/LICENSE.txt).
The Dot Matrix Code (DMC) generation is under Apache license v.2 (see http://www.apache.org/licenses/LICENSE-2.0). 
The Grapevine REST server framework is under Apache license v.2 (see http://www.apache.org/licenses/LICENSE-2.0). */

/* Please notice: the API and REST routes implemented in this version of the source code are not specified and standardised by the
specification Details of the Administration Shell. The hereby stated approach is solely the opinion of its author(s). */

namespace AasxRestServerLibrary
{
    public class AasxHttpContextHelper
    {
        public static String SwitchToAASX = "";
        public static String DataPath = ".";

        public AdminShellPackageEnv[] Packages = null;

        public AasxHttpHandleStore IdRefHandleStore = new AasxHttpHandleStore();

        #region // Path helpers

        public bool PathEndsWith(string path, string tag)
        {
            return path.Trim().ToLower().TrimEnd(new char[] { '/' }).EndsWith(tag);
        }

        public bool PathEndsWith(Grapevine.Interfaces.Server.IHttpContext context, string tag)
        {
            return PathEndsWith(context.Request.PathInfo, tag);
        }

        // see also: https://stackoverflow.com/questions/33619469/how-do-i-write-a-regular-expression-to-route-traffic-with-grapevine-when-my-requ

        public Match PathInfoRegexMatch(MethodBase methodWithRestRoute, string input)
        {
            if (methodWithRestRoute == null)
                return null;
            string piRegex = null;
            foreach (var attr in methodWithRestRoute.GetCustomAttributes<RestRoute>())
                if (attr.PathInfo != null)
                    piRegex = attr.PathInfo;
            if (piRegex == null)
                return null;
            var m = Regex.Match(input, piRegex);
            return m;
        }

        public List<AasxHttpHandleIdentification> CreateHandlesFromQueryString(System.Collections.Specialized.NameValueCollection queryStrings)
        {
            // start
            var res = new List<AasxHttpHandleIdentification>();
            if (queryStrings == null)
                return res;

            // over all query strings
            foreach (var kr in queryStrings.AllKeys)
            {
                try
                {
                    var k = kr.Trim().ToLower();
                    var v = queryStrings[k];
                    if (k.StartsWith("q") && k.Length > 1 && v.Contains(','))
                    {
                        var vl = v.Split(',');
                        if (vl.Length == 2)
                        {
                            var id = new AdminShell.Identification(vl[0], vl[1]);
                            var h = new AasxHttpHandleIdentification(id, "@" + k);
                            res.Add(h);
                        }
                    }
                }
                catch { }
            }

            // done
            return res;
        }

        public List<AasxHttpHandleIdentification> CreateHandlesFromRawUrl(string rawUrl)
        {
            // start
            var res = new List<AasxHttpHandleIdentification>();
            if (rawUrl == null)
                return res;

            // un-escape
            var url = System.Uri.UnescapeDataString(rawUrl);

            // split for query string traditional
            var i = url.IndexOf('?');
            if (i < 0 || i == url.Length - 1)
                return res;
            var query = url.Substring(i + 1);

            // try make a Regex wonder, again
            var m = Regex.Match(query, @"(\s*([^&]+)(&|))+");
            if (m.Success && m.Groups.Count >= 3 && m.Groups[2].Captures != null)
                foreach (var cp in m.Groups[2].Captures)
                {
                    var m2 = Regex.Match(cp.ToString(), @"\s*(\w+)\s*=\s*([^,]+),(.+)$");
                    if (m2.Success && m2.Groups.Count >= 4)
                    {
                        var k = m2.Groups[1].ToString();
                        var idt = m2.Groups[2].ToString();
                        var ids = m2.Groups[3].ToString();

                        var id = new AdminShell.Identification(idt, ids);
                        var h = new AasxHttpHandleIdentification(id, "@" + k);
                        res.Add(h);
                    }
                }

            // done
            return res;
        }

        #endregion

        #region // Access package structures

        public FindAasReturn FindAAS(string aasid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            FindAasReturn findAasReturn = new FindAasReturn();

            if (Packages == null)
                return null;

            if (Regex.IsMatch(aasid, @"^\d+$")) // only number, i.e. index
            {
                // Index
                int i = Convert.ToInt32(aasid);

                if (i > Packages.Length)
                    return null;

                if (Packages[i] == null || Packages[i].AasEnv == null || Packages[i].AasEnv.AdministrationShells == null
                    || Packages[i].AasEnv.AdministrationShells.Count < 1)
                    return null;

                findAasReturn.aas = Packages[i].AasEnv.AdministrationShells[0];
                findAasReturn.iPackage = i;
            }
            else
            {
                // Name
                if (aasid == "id")
                {
                    findAasReturn.aas = Packages[0].AasEnv.AdministrationShells[0];
                    findAasReturn.iPackage = 0;
                }
                else
                {
                    for (int i = 0; i < Packages.Length; i++)
                    {
                        if (Packages[i] != null)
                        {
                            if (Packages[i].AasEnv.AdministrationShells[0].idShort == aasid)
                            {
                                findAasReturn.aas = Packages[i].AasEnv.AdministrationShells[0];
                                findAasReturn.iPackage = i;
                                break;
                            }
                        }
                    }
                }
            }

            return findAasReturn;


            // trivial
            /*
            if (Packages[0] == null || Packages[0].AasEnv == null || Packages[0].AasEnv.AdministrationShells == null || Packages[0].AasEnv.AdministrationShells.Count < 1)
                return null;

            // default aas?
            if (aasid == null || aasid.Trim() == "" || aasid.Trim().ToLower() == "id")
                return Packages[0].AasEnv.AdministrationShells[0];


            var specialHandles = this.CreateHandlesFromRawUrl(rawUrl);
            var handleId = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(aasid, specialHandles);
            if (handleId != null && handleId.identification != null)
                return Packages[0].AasEnv.FindAAS(handleId.identification);

            // no, iterate over idShort
            return Packages[0].AasEnv.FindAAS(aasid);
            */
        }

        public AdminShell.SubmodelRef FindSubmodelRefWithinAas(FindAasReturn findAasReturn, string smid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            // trivial
            if (Packages[findAasReturn.iPackage] == null || Packages[findAasReturn.iPackage].AasEnv == null || findAasReturn.aas == null || smid == null || smid.Trim() == "")
                return null;

            // via handle
            var specialHandles = this.CreateHandlesFromRawUrl(rawUrl);
            var handleId = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(smid, specialHandles);

            // no, iterate & find
            foreach (var smref in findAasReturn.aas.submodelRefs)
            {
                if (handleId != null && handleId.identification != null)
                {
                    if (smref.Matches(handleId.identification))
                        return smref;
                }
                else
                {
                    var sm = this.Packages[findAasReturn.iPackage].AasEnv.FindSubmodel(smref);
                    if (sm != null && sm.idShort != null && sm.idShort.Trim().ToLower() == smid.Trim().ToLower())
                        return smref;
                }
            }

            // no
            return null;
        }

        public AdminShell.Submodel FindSubmodelWithinAas(FindAasReturn findAasReturn, string smid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            // trivial
            if (Packages[findAasReturn.iPackage] == null || Packages[findAasReturn.iPackage].AasEnv == null || findAasReturn.aas == null || smid == null || smid.Trim() == "")
                return null;

            // via handle
            var specialHandles = this.CreateHandlesFromRawUrl(rawUrl);
            var handleId = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(smid, specialHandles);
            if (handleId != null && handleId.identification != null)
                return Packages[findAasReturn.iPackage].AasEnv.FindSubmodel(handleId.identification);

            // no, iterate & find

            foreach (var smref in findAasReturn.aas.submodelRefs)
            {
                var sm = this.Packages[findAasReturn.iPackage].AasEnv.FindSubmodel(smref);
                if (sm != null && sm.idShort != null && sm.idShort.Trim().ToLower() == smid.Trim().ToLower())
                    return sm;
            }

            // no
            return null;
        }


        public AdminShell.Submodel FindSubmodelWithinAas(string aasid, string smid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            AdminShell.AdministrationShell aas = null;
            int iPackage = -1;

            if (Packages == null)
                return null;

            if (Regex.IsMatch(aasid, @"^\d+$")) // only number, i.e. index
            {
                // Index
                int i = Convert.ToInt32(aasid);

                if (i > Packages.Length)
                    return null;

                if (Packages[i] == null || Packages[i].AasEnv == null || Packages[i].AasEnv.AdministrationShells == null
                    || Packages[i].AasEnv.AdministrationShells.Count < 1)
                    return null;

                aas = Packages[i].AasEnv.AdministrationShells[0];
                iPackage = i;
            }
            else
            {
                // Name
                if (aasid == "id")
                {
                    aas = Packages[0].AasEnv.AdministrationShells[0];
                    iPackage = 0;
                }
                else
                {
                    for (int i = 0; i < Packages.Length; i++)
                    {
                        if (Packages[i] != null)
                        {
                            if (Packages[i].AasEnv.AdministrationShells[0].idShort == aasid)
                            {
                                aas = Packages[i].AasEnv.AdministrationShells[0];
                                iPackage = i;
                                break;
                            }
                        }
                    }
                }
            }

            if (aas == null)
                return null;

            // no, iterate & find

            foreach (var smref in aas.submodelRefs)
            {
                var sm = this.Packages[iPackage].AasEnv.FindSubmodel(smref);
                if (sm != null && sm.idShort != null && sm.idShort.Trim().ToLower() == smid.Trim().ToLower())
                    return sm;
            }

            // no
            return null;
        }



        public AdminShell.Submodel FindSubmodelWithoutAas(string smid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            // trivial
            if (Packages[0] == null || Packages[0].AasEnv == null || smid == null || smid.Trim() == "")
                return null;

            // via handle
            var specialHandles = this.CreateHandlesFromRawUrl(rawUrl);
            var handleId = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(smid, specialHandles);
            if (handleId != null && handleId.identification != null)
                return Packages[0].AasEnv.FindSubmodel(handleId.identification);

            // no, iterate & find
            foreach (var sm in this.Packages[0].AasEnv.Submodels)
            {
                if (sm != null && sm.idShort != null && sm.idShort.Trim().ToLower() == smid.Trim().ToLower())
                    return sm;
            }

            // no
            return null;
        }

        public AdminShell.ConceptDescription FindCdWithoutAas(FindAasReturn findAasReturn, string cdid, System.Collections.Specialized.NameValueCollection queryStrings = null, string rawUrl = null)
        {
            // trivial
            if (Packages[findAasReturn.iPackage] == null || Packages[findAasReturn.iPackage].AasEnv == null || findAasReturn.aas == null || cdid == null || cdid.Trim() == "")
                return null;

            // via handle
            var specialHandles = this.CreateHandlesFromRawUrl(rawUrl);
            var handleId = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(cdid, specialHandles);
            if (handleId != null && handleId.identification != null)
                return Packages[findAasReturn.iPackage].AasEnv.FindConceptDescription(handleId.identification);

            // no, iterate & find
            foreach (var cd in Packages[findAasReturn.iPackage].AasEnv.ConceptDescriptions)
            {
                if (cd.idShort != null && cd.idShort.Trim().ToLower() == cdid.Trim().ToLower())
                    return cd;
            }

            // no
            return null;
        }


        public class FindSubmodelElementResult
        {
            public AdminShell.Referable elem = null;
            public AdminShell.SubmodelElementWrapper wrapper = null;
            public AdminShell.Referable parent = null;

            public FindSubmodelElementResult(AdminShell.Referable elem = null, AdminShell.SubmodelElementWrapper wrapper = null, AdminShell.Referable parent = null)
            {
                this.elem = elem;
                this.wrapper = wrapper;
                this.parent = parent;
            }
        }

        public FindSubmodelElementResult FindSubmodelElement(AdminShell.Referable parent, List<AdminShell.SubmodelElementWrapper> wrappers, string[] elemids, int elemNdx = 0)
        {
            // trivial
            if (wrappers == null || elemids == null || elemNdx >= elemids.Length)
                return null;

            // dive into each
            foreach (var smw in wrappers)
                if (smw.submodelElement != null)
                {
                    // idShort need to match
                    if (smw.submodelElement.idShort.Trim().ToLower() != elemids[elemNdx].Trim().ToLower())
                        continue;

                    // leaf
                    if (elemNdx == elemids.Length - 1)
                    {
                        return new FindSubmodelElementResult(elem: smw.submodelElement, wrapper: smw, parent: parent);
                    }
                    else
                    {
                        // recurse into?
                        var xsmc = smw.submodelElement as AdminShell.SubmodelElementCollection;
                        if (xsmc != null)
                        {
                            var r = FindSubmodelElement(xsmc, xsmc.value, elemids, elemNdx + 1);
                            if (r != null)
                                return r;
                        }

                        var xop = smw.submodelElement as AdminShell.Operation;
                        if (xop != null)
                        {
                            var w2 = new List<AdminShell.SubmodelElementWrapper>();
                            for (int i = 0; i < 2; i++)
                                foreach (var opv in xop[i])
                                    if (opv.value != null)
                                        w2.Add(opv.value);

                            var r = FindSubmodelElement(xop, w2, elemids, elemNdx + 1);
                            if (r != null)
                                return r;
                        }

                        var xent = smw.submodelElement as AdminShell.Entity;
                        if (xent != null)
                        {
                            var r = FindSubmodelElement(xsmc, xent.statements, elemids, elemNdx + 1);
                            if (r != null)
                                return r;
                        }
                    }
                }

            // nothing
            return null;
        }

        #endregion

        #region // Generate responses

        public class AdaptiveFilterContractResolver : DefaultContractResolver
        {
            public bool AasHasViews = true;
            public bool BlobHasValue = true;
            public bool SubmodelHasElements = true;
            public bool SmcHasValue = true;
            public bool OpHasVariables = true;

            public AdaptiveFilterContractResolver() { }

            public AdaptiveFilterContractResolver(bool deep = true, bool complete = true)
            {
                if (!deep)
                {
                    this.SubmodelHasElements = false;
                    this.SmcHasValue = false;
                    this.OpHasVariables = false;
                }
                if (!complete)
                {
                    this.AasHasViews = false;
                    this.BlobHasValue = false;
                }

            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);

                if (!BlobHasValue && property.DeclaringType == typeof(AdminShell.Blob) && property.PropertyName == "value")
                    property.ShouldSerialize = instance => { return false; };

                if (!SubmodelHasElements && property.DeclaringType == typeof(AdminShell.Submodel) && property.PropertyName == "submodelElements")
                    property.ShouldSerialize = instance => { return false; };

                if (!SmcHasValue && property.DeclaringType == typeof(AdminShell.SubmodelElementCollection) && property.PropertyName == "value")
                    property.ShouldSerialize = instance => { return false; };

                if (!OpHasVariables && property.DeclaringType == typeof(AdminShell.Operation) && (property.PropertyName == "in" || property.PropertyName == "out"))
                    property.ShouldSerialize = instance => { return false; };

                if (!AasHasViews && property.DeclaringType == typeof(AdminShell.AdministrationShell) && property.PropertyName == "views")
                    property.ShouldSerialize = instance => { return false; };

                return property;
            }
        }

        protected static void SendJsonResponse(Grapevine.Interfaces.Server.IHttpContext context, object obj, IContractResolver contractResolver = null)
        {
            var settings = new JsonSerializerSettings();
            if (contractResolver != null)
                settings.ContractResolver = contractResolver;
            var json = JsonConvert.SerializeObject(obj, Formatting.Indented, settings);
            var buffer = context.Request.ContentEncoding.GetBytes(json);
            var length = buffer.Length;

            var queryString = context.Request.QueryString;
            string refresh = queryString["refresh"];
            if (refresh != null && refresh != "")
            {
                context.Response.Headers.Remove("Refresh");
                context.Response.Headers.Add("Refresh", refresh);
            }

            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Credentials", "true");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "origin, content-type, accept, authorization");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS, HEAD");

            context.Response.ContentType = ContentType.JSON;
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = length;
            context.Response.SendResponse(buffer);
        }

        protected static void SendTextResponse(Grapevine.Interfaces.Server.IHttpContext context, string txt, string mimeType = null)
        {
            var queryString = context.Request.QueryString;
            string refresh = queryString["refresh"];
            if (refresh != null && refresh != "")
            {
                context.Response.Headers.Remove("Refresh");
                context.Response.Headers.Add("Refresh", refresh);
            }

            context.Response.ContentType = ContentType.TEXT;
            if (mimeType != null)
                context.Response.Advanced.ContentType = mimeType;
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = txt.Length;
            context.Response.SendResponse(txt);
        }

        protected static void SendStreamResponse(IHttpContext context, Stream stream,
            string headerAttachmentFileName = null)
        {
            context.Response.ContentType = ContentType.APPLICATION;
            //// context.Response.SendChunked = true;
            context.Response.ContentLength64 = stream.Length;

            if (headerAttachmentFileName != null)
                context.Response.AddHeader("Content-Disposition", $"attachment; filename={headerAttachmentFileName}");

            stream.CopyTo(context.Response.Advanced.OutputStream);
            context.Response.Advanced.Close();
        }

        protected static void SendRedirectResponse(Grapevine.Interfaces.Server.IHttpContext context, string redirectUrl)
        {
            context.Response.AppendHeader("redirectInfo", "URL");
            context.Response.Redirect(redirectUrl);
            context.Response.SendResponse(HttpStatusCode.TemporaryRedirect, redirectUrl);
        }


        #endregion

        #region AAS and Asset

        public void EvalGetAasAndAsset(IHttpContext context, string aasid, bool deep = false, bool complete = false)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (false && withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (accessrights == null)
                {
                    res.error = "You are not authorized for this operation!";
                    SendJsonResponse(context, res);
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access the first AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with id '{aasid}' found.");
                return;
            }

            // try to get the asset as well
            var asset = this.Packages[findAasReturn.iPackage].AasEnv.FindAsset(findAasReturn.aas.assetRef);

            // result
            res.AAS = findAasReturn.aas;
            res.Asset = asset;

            // return as JSON
            var cr = new AdminShellConverters.AdaptiveFilterContractResolver(deep: deep, complete: complete);
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res, cr);
        }

        public void EvalGetAasEnv(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aasenv", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            if (this.Packages[0] == null || this.Packages[0].AasEnv == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // access the first AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with id '{aasid}' found.");
                return;
            }

            // create a new, filtered AasEnv
            AdminShell.AdministrationShellEnv copyenv = null;
            try
            {
                copyenv = AdminShell.AdministrationShellEnv.CreateFromExistingEnv(this.Packages[findAasReturn.iPackage].AasEnv, filterForAas: new List<AdminShell.AdministrationShell>(new AdminShell.AdministrationShell[] { findAasReturn.aas }));
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(Grapevine.Shared.HttpStatusCode.BadRequest, $"Cannot filter aas envioronment: {ex.Message}.");
                return;
            }

            // return as FILE
            try
            {
                using (var ms = new MemoryStream())
                {
                    // build a file name
                    var fn = "aasenv.json";
                    if (findAasReturn.aas.idShort != null)
                        fn = findAasReturn.aas.idShort + "." + fn;
                    // serialize via helper
                    var jsonwriter = copyenv.SerialiazeJsonToStream(new StreamWriter(ms), leaveJsonWriterOpen: true);
                    // write out again
                    ms.Position = 0;
                    SendStreamResponse(context, ms, Path.GetFileName(fn));
                    // bit ugly
                    jsonwriter.Close();
                }
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot serialize and send aas envioronment: {ex.Message}.");
                return;
            }
            context.Response.StatusCode = HttpStatusCode.Ok;
        }


        public void EvalGetAasThumbnail(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/thumbnail", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            if (this.Packages[0] == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // access the first AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with id '{aasid}' found.");
                return;
            }

            // access the thumbnail
            // Note: in this version, the thumbnail is not specific to the AAS, but maybe in later versions ;-)
            Uri thumbUri = null;
            var thumbStream = this.Packages[findAasReturn.iPackage].GetLocalThumbnailStream(ref thumbUri);
            if (thumbStream == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No thumbnail available in package.");
                return;
            }

            // return as FILE
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendStreamResponse(context, thumbStream, Path.GetFileName(thumbUri.ToString() ?? ""));
            thumbStream.Close();
        }

        public void EvalPutAas(IHttpContext context)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aas", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // list of Identification
            AdminShell.AdministrationShell aas = null;
            try
            {
                aas = Newtonsoft.Json.JsonConvert.DeserializeObject<AdminShell.AdministrationShell>(context.Request.Payload);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }

            // need id for idempotent behaviour
            if (aas.identification == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Identification of entity is (null); PUT cannot be performed.");
                return;
            }

            // datastructure update
            context.Server.Logger.Debug($"Putting AdministrationShell with idShort {aas.idShort ?? "--"} and id {aas.identification?.ToString() ?? "--"}");

            bool emptyPackageAvailable = false;
            int emptyPackageIndex = -1;
            for (int envi = 0; envi < this.Packages.Length; envi++)
            {
                if (this.Packages[envi] != null)
                {
                    var existingAas = this.Packages[envi].AasEnv.FindAAS(aas.identification);
                    if (existingAas != null)
                    {
                        this.Packages[envi].AasEnv.AdministrationShells.Remove(existingAas);
                        this.Packages[envi].AasEnv.AdministrationShells.Add(aas);
                        SendTextResponse(context, "OK (update, index=" + envi + ")");
                        return;
                    }
                }
                else
                {
                    if (!emptyPackageAvailable)
                    {
                        emptyPackageAvailable = true;
                        emptyPackageIndex = envi;
                    }
                }
            }

            if (emptyPackageAvailable)
            {
                this.Packages[emptyPackageIndex] = new AdminShellPackageEnv();
                this.Packages[emptyPackageIndex].AasEnv.AdministrationShells.Add(aas);
                SendTextResponse(context, "OK (new, index=" + emptyPackageIndex + ")");
                return;
            }

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "Error: not added since datastructure completely filled already");
        }

        public void EvalPutAasxOnServer(IHttpContext context)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aasx", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            Console.WriteLine("EvalPutAasxOnServer: " + context.Request.Payload);
            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            AasxFileInfo file = Newtonsoft.Json.JsonConvert.DeserializeObject<AasxFileInfo>(context.Request.Payload);
            if (!file.path.ToLower().EndsWith(".aasx"))
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Not a path ending with \".aasx\"...:{file.path}. Aborting...");
                return;
            }

            AdminShellPackageEnv aasEnv = null;
            try
            {
                aasEnv = new AdminShellPackageEnv(file.path, true);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot open {file.path}. Aborting... {ex.Message}");
                return;
            }

            if (file.instantiateTemplate)
            {
                if (file.instancesIdentificationSuffix == null)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Received no identification suffix. Aborting...");
                    return;
                }
                else
                {
                    Console.WriteLine("EvalPutAasxOnServer: file.instancesIdentificationSuffix = " + file.instancesIdentificationSuffix);

                    // instantiate aas
                    foreach (var aas in aasEnv.AasEnv.AdministrationShells)
                    {
                        aas.idShort += file.instancesIdentificationSuffix;
                        aas.identification.id += file.instancesIdentificationSuffix;
                        aas.assetRef[0].value += file.instancesIdentificationSuffix;
                        foreach (var smref in aas.submodelRefs)
                        {
                            foreach (var key in smref.Keys)
                            {
                                key.value += file.instancesIdentificationSuffix;
                            }
                        }
                    }

                    // instantiate asset
                    foreach (var asset in aasEnv.AasEnv.Assets)
                    {
                        asset.idShort += file.instancesIdentificationSuffix;
                        asset.identification.id += file.instancesIdentificationSuffix;
                    }

                    // instantiate submodel
                    foreach (var submodel in aasEnv.AasEnv.Submodels)
                    {
                        submodel.identification.id += file.instancesIdentificationSuffix;
                        if (file.instantiateSubmodelsIdShort)
                        {
                            submodel.idShort += file.instancesIdentificationSuffix;
                        }
                    }
                }
            }

            string aasIdShort = "";
            try
            {
                aasIdShort = aasEnv.AasEnv.AdministrationShells[0].idShort;
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot find idShort in {file.path}. Aborting... {ex.Message}");
                return;
            }

            var findAasReturn = this.FindAAS(aasIdShort, context.Request.QueryString, context.Request.RawUrl);
            Console.WriteLine("FindAAS() with idShort \"" + aasIdShort + "\" yields package-index " + findAasReturn.iPackage);

            if (findAasReturn.aas == null)
            {
                for (int envi = 0; envi < this.Packages.Length; envi++)
                {
                    if (this.Packages[envi] == null)
                    {
                        this.Packages[envi] = aasEnv;
                        Program.envFileName[envi] = file.path;
                        context.Response.StatusCode = HttpStatusCode.Ok;
                        SendTextResponse(context, "OK (new, index=" + envi + ")");
                        return;
                    }
                }
                context.Response.StatusCode = HttpStatusCode.NotFound;
                SendTextResponse(context, "Failed: Server used to capacity.");
                return;
            }
            else
            {
                Packages[findAasReturn.iPackage] = aasEnv;
                Program.envFileName[findAasReturn.iPackage] = file.path;
                context.Response.StatusCode = HttpStatusCode.Ok;
                SendTextResponse(context, "OK (update, index=" + findAasReturn.iPackage + ")");
                return;
            }
        }

        public void EvalPutAasxToFilesystem(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aasx", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            Console.WriteLine("EvalPutAasxToFilesystem");
            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            AasxFileInfo file = Newtonsoft.Json.JsonConvert.DeserializeObject<AasxFileInfo>(context.Request.Payload);
            Console.WriteLine("EvalPutAasxToFilesystem: " + JsonConvert.SerializeObject(file.path));
            if (!file.path.ToLower().EndsWith(".aasx"))
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Not a path ending with \".aasx\"...:{file.path}. Aborting...");
                return;
            }

            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            Console.WriteLine("FindAAS() with idShort \"" + aasid + "\" yields package-index " + findAasReturn.iPackage);

            if (findAasReturn.aas == null)
            {
                context.Response.StatusCode = HttpStatusCode.NotFound;
                SendTextResponse(context, "Failed: AAS not found.");
                return;
            }
            else
            {
                try
                {
                    Packages[findAasReturn.iPackage].SaveAs(file.path, false, AdminShellPackageEnv.PreferredFormat.Json, null);
                    Program.envFileName[findAasReturn.iPackage] = file.path;
                    context.Response.StatusCode = HttpStatusCode.Ok;
                    SendTextResponse(context, "OK (saved)");
                    return;
                }
                catch (Exception ex)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot save in {file.path}. Aborting... {ex.Message}");
                    return;
                }
            }
        }

        public void EvalPutAasxReplacePackage(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;
            string accessrights = null;

            var aasInfo = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);

            // check authentication
            if (withAuthentification)
            {
                accessrights = SecurityCheck(context, ref index);

                var aas = Program.env[aasInfo.iPackage].AasEnv.AdministrationShells[0];
                if (!checkAccessRights(context, accessrights, "/aasx", "UPDATE", "", "aas", aas))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentLength64 < 1)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload for replace AASX.");
                return;
            }

            // find package index to replace
            Console.WriteLine("FindAAS() with idShort \"" + aasid + "\" yields package-index " + aasInfo.iPackage);
            var packIndex = aasInfo.iPackage;
            if (packIndex < 0 || packIndex >= Packages.Length)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"AASX package to be replaced not found. Aborting!");
                return;
            }

            /*
            if (withAuthentification)
            {
                string idshort = AasxServer.Program.env[packIndex].AasEnv.AdministrationShells[0].idShort;
                string aasRights = "NONE";
                if (securityRightsAAS.Count != 0)
                    aasRights = securityRightsAAS[idshort];
                if (!checkAccessRights(context, accessrights, aasRights))
                {
                    return;
                }
            }
            */

            var packFn = Packages[packIndex].Filename;
            Console.WriteLine($"Will replace AASX package on server: {packFn}");

            // make temp file
            var tempFn = System.IO.Path.GetTempFileName().Replace(".tmp", ".aasx");
            try
            {
                var ba = Convert.FromBase64String(context.Request.Payload);
                File.WriteAllBytes(tempFn, ba);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot save AASX temporarily in {tempFn}. Aborting... {ex.Message}");
                return;
            }

            lock (Program.changeAasxFile)
            {
                // close old and renamed
                try
                {
                    // free to overwrite
                    Packages[packIndex].Close();

                    // copy to back (rename experienced to be more error-prone)
                    File.Copy(packFn, packFn + ".bak", overwrite: true);
                }
                catch (Exception ex)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot close/ backup old AASX {packFn}. Aborting... {ex.Message}");
                    return;
                }

                // replace exactly the file
                try
                {
                    // copy into same location
                    File.Copy(tempFn, packFn, overwrite: true);

                    // open again
                    var newAasx = new AdminShellPackageEnv(packFn, true);
                    if (newAasx != null)
                        Packages[packIndex] = newAasx;
                    else
                    {
                        context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot load new package {tempFn} for replacing via PUT. Aborting.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot replace AASX {packFn} with new {tempFn}. Aborting... {ex.Message}");
                    return;
                }

                if (withAuthentification)
                {
                    securityInit();
                }
            }

            Program.signalNewData(2);
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK (saved)");
        }

        public void EvalGetAasxByAssetId(IHttpContext context)
        {
            string path = context.Request.PathInfo;
            string[] split = path.Split('/');
            string node = split[2];
            string assetId = split[3].ToUpper();

            for (int envi = 0; envi < Packages.Length; envi++)
            {
                if (this.Packages[envi] != null)
                {
                    foreach (var aas in this.Packages[envi].AasEnv.AdministrationShells)
                    {
                        if (aas.assetRef != null)
                        {
                            var asset = Program.env[envi].AasEnv.FindAsset(aas.assetRef);
                            if (asset != null)
                            {
                                string url = System.Net.WebUtility.UrlEncode(asset.identification.id).ToUpper();
                                if (assetId == url)
                                {
                                    string headers = context.Request.Headers.ToString();
                                    string token = context.Request.Headers.Get("accept");
                                    if (token == null || token != "application/aas")
                                    {
                                        // Human by browser
                                        string text = "";

                                        text += "<strong>" + "This is the human readable page for your asset" + "</strong><br><br>";

                                        text += "AssetID = " + System.Net.WebUtility.UrlDecode(assetId) + "<br><br>";

                                        lock (Program.changeAasxFile)
                                        {
                                            string detailsImage = "";
                                            System.IO.Stream s = null;
                                            try
                                            {
                                                s = Program.env[envi].GetLocalThumbnailStream();
                                            }
                                            catch { }
                                            if (s != null)
                                            {
                                                using (var m = new System.IO.MemoryStream())
                                                {
                                                    s.CopyTo(m);
                                                    detailsImage = System.Convert.ToBase64String(m.ToArray());
                                                }
                                                if (detailsImage != "")
                                                {
                                                    text += "<br>" +
                                                        "Your product image:" +
                                                        "<div><img src=data:image;base64," +
                                                        detailsImage +
                                                        " style=\"max-width: 25%;\" alt=\"Details Image\" /></div>";
                                                }
                                            }
                                        }
                                        text += "<br>";

                                        // var link = "http://" + Program.hostPort + "/server/getaasxbyassetid/" + assetId;
                                        var link = Program.externalRest + "/server/getaasxbyassetid/" + assetId;

                                        text += "Please open AAS in AASX Package Explorer by: File / Other Connect Options / Connect via REST:<br>" +
                                            "<a href= \"" + link + "\" target=\"_blank\">" +
                                            link + "</a>" + "<br><br>";

                                        text += "Please use Postman to get raw data:<br>GET " +
                                            "<a href= \"" + link + "\" target=\"_blank\">" +
                                            link + "</a>" + "<br>" +
                                        "and set Headers / Accept application/aas" + "<br><br>";

                                        context.Response.ContentType = ContentType.HTML;
                                        context.Response.ContentEncoding = System.Text.Encoding.UTF8;
                                        context.Response.SendResponse(text);
                                        return;
                                    }

                                    EvalGetAASX(context, envi);
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with assetId '{assetId}' found.");
        }

        public void EvalDeleteAasAndAsset(IHttpContext context, string aasid, bool deleteAsset = false)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aas", "DELETE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // datastructure update
            if (this.Packages[0] == null || this.Packages[0].AasEnv == null || this.Packages[0].AasEnv.AdministrationShells == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                return;
            }

            // find the asset
            var asset = this.Packages[findAasReturn.iPackage].AasEnv.FindAsset(findAasReturn.aas.assetRef);

            // delete
            context.Server.Logger.Debug($"Deleting AdministrationShell with idShort {findAasReturn.aas.idShort ?? "--"} and id {findAasReturn.aas.identification?.ToString() ?? "--"}");
            this.Packages[findAasReturn.iPackage].AasEnv.AdministrationShells.Remove(findAasReturn.aas);

            if (this.Packages[findAasReturn.iPackage].AasEnv.AdministrationShells.Count == 0)
            {
                this.Packages[findAasReturn.iPackage] = null;
            }
            else
            {
                if (deleteAsset && asset != null)
                {
                    context.Server.Logger.Debug($"Deleting Asset with idShort {asset.idShort ?? "--"} and id {asset.identification?.ToString() ?? "--"}");
                    this.Packages[findAasReturn.iPackage].AasEnv.Assets.Remove(asset);
                }
            }

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK");
        }

        #endregion

        #region // Asset links

        public void EvalGetAssetLinks(IHttpContext context, string assetid)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aas", "READ"))
                {
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // trivial
            if (assetid == null)
            {
                context.Response.StatusCode = HttpStatusCode.NotFound;
                return;
            }

            // do a manual search
            var res = new List<ExpandoObject>();
            var specialHandles = this.CreateHandlesFromQueryString(context.Request.QueryString);
            var handle = IdRefHandleStore.ResolveSpecific<AasxHttpHandleIdentification>(assetid, specialHandles);
            if (handle != null && handle.identification != null)
            {
                foreach (var aas in this.Packages[0].AasEnv.AdministrationShells)
                    if (aas.assetRef != null && aas.assetRef.Matches(handle.identification))
                    {
                        dynamic o = new ExpandoObject();
                        o.identification = aas.identification;
                        o.idShort = aas.idShort;
                        res.Add(o);
                    }
            }
            else
            {
                foreach (var aas in this.Packages[0].AasEnv.AdministrationShells)
                    if (aas.idShort != null && aas.idShort.Trim() != "" && aas.idShort.Trim().ToLower() == assetid.Trim().ToLower())
                    {
                        dynamic o = new ExpandoObject();
                        o.identification = aas.identification;
                        o.idShort = aas.idShort;
                        res.Add(o);
                    }
            }

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalPutAsset(IHttpContext context)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aas", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // de-serialize asset
            AdminShell.Asset asset = null;
            try
            {
                asset = Newtonsoft.Json.JsonConvert.DeserializeObject<AdminShell.Asset>(context.Request.Payload);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }

            // need id for idempotent behaviour
            if (asset.identification == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Identification of entity is (null); PUT cannot be performed.");
                return;
            }

            // datastructure update
            if (this.Packages[0] == null || this.Packages[0].AasEnv == null || this.Packages[0].AasEnv.Assets == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }
            context.Server.Logger.Debug($"Adding Asset with idShort {asset.idShort ?? "--"}");
            var existingAsset = this.Packages[0].AasEnv.FindAsset(asset.identification);
            if (existingAsset != null)
                this.Packages[0].AasEnv.Assets.Remove(existingAsset);
            this.Packages[0].AasEnv.Assets.Add(asset);

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + ((existingAsset != null) ? " (updated)" : " (new)"));
        }

        public void EvalPutAssetToAas(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aas", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                Console.WriteLine("ERROR PUT: No payload or content type is not JSON.");
                return;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                Console.WriteLine("ERROR PUT: No AAS with idShort '{0}' found.", aasid);
                return;
            }

            // de-serialize asset
            AdminShell.Asset asset = null;
            try
            {
                asset = Newtonsoft.Json.JsonConvert.DeserializeObject<AdminShell.Asset>(context.Request.Payload);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }

            // need id for idempotent behaviour
            if (asset.identification == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Identification of entity is (null); PUT cannot be performed.");
                Console.WriteLine("ERROR PUT: Identification of entity is (null); PUT cannot be performed.");
                return;
            }

            // datastructure update
            if (this.Packages[findAasReturn.iPackage] == null || this.Packages[findAasReturn.iPackage].AasEnv == null || this.Packages[findAasReturn.iPackage].AasEnv.Assets == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // add Asset
            context.Server.Logger.Debug($"Adding Asset with idShort {asset.idShort ?? "--"} and id {asset.identification?.ToString() ?? "--"}");
            var existingAsset = this.Packages[findAasReturn.iPackage].AasEnv.FindAsset(asset.identification);
            if (existingAsset != null)
                this.Packages[findAasReturn.iPackage].AasEnv.Assets.Remove(existingAsset);
            this.Packages[findAasReturn.iPackage].AasEnv.Assets.Add(asset);

            // add AssetRef to AAS        
            findAasReturn.aas.assetRef = new AdminShellV20.AssetRef(new AdminShellV20.Reference(new AdminShellV20.Key("Asset", true, asset.identification.idType, asset.identification.id)));

            Console.WriteLine("{0} Received PUT Asset {1}", countPut++, asset.idShort);

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + ((existingAsset != null) ? " (updated)" : " (new)"));
        }
        #endregion

        #region // List of Submodels

        public class GetSubmodelsItem
        {
            public AdminShell.Identification id = new AdminShell.Identification();
            public string idShort = "";
            public string kind = "";

            public GetSubmodelsItem() { }

            public GetSubmodelsItem(AdminShell.Identification id, string idShort, string kind)
            {
                this.id = id;
                this.idShort = idShort;
                this.kind = kind;
            }

            public GetSubmodelsItem(AdminShell.Identifiable idi, string kind)
            {
                this.id = idi.identification;
                this.idShort = idi.idShort;
                this.kind = kind;
            }
        }

        public void EvalGetSubmodels(IHttpContext context, string aasid)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodels", "READ"))
                {
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                return;
            }

            // build a list of results
            var res = new List<GetSubmodelsItem>();

            // get all submodels
            foreach (var smref in findAasReturn.aas.submodelRefs)
            {
                var sm = this.Packages[findAasReturn.iPackage].AasEnv.FindSubmodel(smref);
                if (sm != null)
                {
                    res.Add(new GetSubmodelsItem(sm, sm.kind.kind));
                }
            }

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        static long countPut = 0;
        public void EvalPutSubmodel(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodels", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                Console.WriteLine("ERROR PUT: No payload or content type is not JSON.");
                return;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                Console.WriteLine("ERROR PUT: No AAS with idShort '{0}' found.", aasid);
                return;
            }

            // de-serialize Submodel
            AdminShell.Submodel submodel = null;
            try
            {
                using (TextReader reader = new StringReader(context.Request.Payload))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    serializer.Converters.Add(new AdminShellConverters.JsonAasxConverter("modelType", "name"));
                    submodel = (AdminShell.Submodel)serializer.Deserialize(reader, typeof(AdminShell.Submodel));
                }
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                Console.WriteLine("ERROR PUT: Cannot deserialize payload.");
                return;
            }

            // need id for idempotent behaviour
            if (submodel.identification == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Identification of entity is (null); PUT cannot be performed.");
                Console.WriteLine("ERROR PUT: Identification of entity is (null); PUT cannot be performed.");
                return;
            }

            // datastructure update
            if (this.Packages[findAasReturn.iPackage] == null || this.Packages[findAasReturn.iPackage].AasEnv == null || this.Packages[findAasReturn.iPackage].AasEnv.Assets == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // add Submodel
            context.Server.Logger.Debug($"Adding Submodel with idShort {submodel.idShort ?? "--"} and id {submodel.identification?.ToString() ?? "--"}");
            var existingSm = this.Packages[findAasReturn.iPackage].AasEnv.FindSubmodel(submodel.identification);
            if (existingSm != null)
            {
                int indexOfExistingSm = this.Packages[findAasReturn.iPackage].AasEnv.Submodels.IndexOf(existingSm);
                this.Packages[findAasReturn.iPackage].AasEnv.Submodels.RemoveAt(indexOfExistingSm);
                this.Packages[findAasReturn.iPackage].AasEnv.Submodels.Insert(indexOfExistingSm, submodel);
            }
            else
            {
                this.Packages[findAasReturn.iPackage].AasEnv.Submodels.Add(submodel);
            }

            // add SubmodelRef to AAS            
            var newsmr = AdminShell.SubmodelRef.CreateNew("Submodel", true, submodel.identification.idType, submodel.identification.id);
            var existsmr = findAasReturn.aas.HasSubmodelRef(newsmr);
            if (!existsmr)
            {
                context.Server.Logger.Debug($"Adding SubmodelRef to AAS with idShort {findAasReturn.aas.idShort ?? "--"} and id {findAasReturn.aas.identification?.ToString() ?? "--"}");
                findAasReturn.aas.AddSubmodelRef(newsmr);
            }

            Console.WriteLine("{0} Received PUT Submodel {1}", countPut++, submodel.idShort);

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + ((existingSm != null) ? " (updated)" : " (new)"));
        }

        public void EvalDeleteSubmodel(IHttpContext context, string aasid, string smid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodels", "DELETE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // access the AAS (absolutely mandatory)
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                return;
            }

            // delete SubmodelRef 1st
            var smref = this.FindSubmodelRefWithinAas(findAasReturn, smid, context.Request.QueryString, context.Request.RawUrl);
            if (smref != null)
            {
                context.Server.Logger.Debug($"Removing SubmodelRef {smid} from AAS with idShort {findAasReturn.aas.idShort ?? "--"} and id {findAasReturn.aas.identification?.ToString() ?? "--"}");
                findAasReturn.aas.submodelRefs.Remove(smref);
            }

            // delete Submodel 2nd
            var sm = this.FindSubmodelWithoutAas(smid, context.Request.QueryString, context.Request.RawUrl);
            if (sm != null)
            {
                context.Server.Logger.Debug($"Removing Submodel {smid} from data structures.");
                this.Packages[findAasReturn.iPackage].AasEnv.Submodels.Remove(sm);
            }

            // simple OK
            var cmt = "";
            if (smref == null && sm == null)
                cmt += " (nothing deleted)";
            cmt += ((smref != null) ? " (SubmodelRef deleted)" : "") + ((sm != null) ? " (Submodel deleted)" : "");
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + cmt);
        }

        #endregion

        #region // Submodel Complete
        static long countGet = 0;

        public void EvalGetSubmodelContents(IHttpContext context, string aasid, string smid, bool deep = false, bool complete = false)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodels", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);
            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            Console.WriteLine("{0} Received GET Submodel {1}", countGet++, sm.idShort);

            // return as JSON
            var cr = new AdminShellConverters.AdaptiveFilterContractResolver(deep: deep, complete: complete);
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, sm, cr);
        }

        public void EvalGetSubmodelContentsAsTable(IHttpContext context, string aasid, string smid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodels", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // AAS ENV
            if (this.Packages[0] == null || this.Packages[0].AasEnv == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // make a table
            var table = new List<ExpandoObject>();
            sm.RecurseOnSubmodelElements(null, (o, parents, sme) =>
            {
                // start a row
                dynamic row = new ExpandoObject();

                // defaults
                row.idShorts = "";
                row.typeName = "";
                row.semIdType = "";
                row.semId = "";
                row.shortName = "";
                row.unit = "";
                row.value = "";

                // idShort is a concatenation
                var path = "";
                foreach (var p in parents)
                    path += p.idShort + "/";

                // SubnmodelElement general
                row.idShorts = path + sme.idShort ?? "(-)";
                row.typeName = sme.GetElementName();
                if (sme.semanticId == null || sme.semanticId.Keys == null || sme.semanticId.Keys.Count == 0)
                { }
                else if (sme.semanticId.Keys.Count > 1)
                {
                    row.semId = "(complex)";
                }
                else
                {
                    row.semIdType = sme.semanticId.Keys[0].idType;
                    row.semId = sme.semanticId.Keys[0].value;
                }

                // try find a concept description
                if (sme.semanticId != null)
                {
                    var cd = this.Packages[0].AasEnv.FindConceptDescription(sme.semanticId.Keys);
                    if (cd != null)
                    {
                        var ds = cd.GetIEC61360();
                        if (ds != null)
                        {
                            row.shortName = (ds.shortName == null ? "" : ds.shortName.GetDefaultStr());
                            row.unit = ds.unit ?? "";
                        }
                    }
                }

                // try add a value
                if (sme is AdminShell.Property)
                {
                    var p = sme as AdminShell.Property;
                    row.value = "" + (p.value ?? "") + ((p.valueId != null) ? p.valueId.ToString() : "");
                }

                if (sme is AdminShell.File)
                {
                    var p = sme as AdminShell.File;
                    row.value = "" + p.value;
                }

                if (sme is AdminShell.Blob)
                {
                    var p = sme as AdminShell.Blob;
                    if (p.value.Length < 128)
                        row.value = "" + p.value;
                    else
                        row.value = "(" + p.value.Length + " bytes)";
                }

                if (sme is AdminShell.ReferenceElement)
                {
                    var p = sme as AdminShell.ReferenceElement;
                    row.value = "" + p.value.ToString();
                }

                if (sme is AdminShell.RelationshipElement)
                {
                    var p = sme as AdminShell.RelationshipElement;
                    row.value = "" + (p.first?.ToString() ?? "(-)") + " <-> " + (p.second?.ToString() ?? "(-)");
                }

                // now, add the row
                table.Add(row);

            });

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, table);
        }

        #endregion

        #region // Submodel Elements

        public void EvalGetSubmodelElementContents(IHttpContext context, string aasid, string smid, string[] elemids, bool deep = false, bool complete = false)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string objPath = smid;
                foreach (var el in elemids)
                {
                    objPath += "." + el;
                }

                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "READ", objPath))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // find the right SubmodelElement
            var sme = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
            if (sme == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching element in Submodel found.");
                return;
            }

            // return as JSON
            var cr = new AdminShellConverters.AdaptiveFilterContractResolver(deep: deep, complete: complete);
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, sme, cr);
        }

        public void EvalGetSubmodelElementsBlob(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // find the right SubmodelElement
            var fse = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
            var smeb = fse?.elem as AdminShell.Blob;
            if (smeb == null || smeb.value == null || smeb.value == "")
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching Blob element in Submodel found.");
                return;
            }

            // return as TEXT
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, smeb.value, mimeType: smeb.mimeType);
        }

        private string EvalGetSubmodelElementsProperty_EvalValue(AdminShell.Property smep)
        {
            // access
            if (smep == null)
                return null;

            // try to apply a little bit voodo
            double dblval = 0.0;
            string strval = smep.value;
            if (smep.HasQualifierOfType("DEMO") != null && smep.value != null && smep.valueType != null
                && smep.valueType.Trim().ToLower() == "double"
                && double.TryParse(smep.value, NumberStyles.Any, CultureInfo.InvariantCulture, out dblval))
            {
                // add noise
                dblval += Math.Sin((0.001 * DateTime.UtcNow.Millisecond) * 6.28);
                strval = dblval.ToString(CultureInfo.InvariantCulture);
            }
            return strval;
        }

        private List<ExpandoObject> EvalGetSubmodelElementsProperty_EvalValues(
            AdminShell.SubmodelElementWrapperCollection wrappers)
        {
            // access
            if (wrappers == null)
                return null;
            List<ExpandoObject> res = new List<ExpandoObject>();

            // recurse for results
            wrappers.RecurseOnSubmodelElements(null, new List<AdminShell.SubmodelElement>(),
                (_, pars, el) =>
                {
                    if (el is AdminShell.Property smep && pars != null)
                    {
                        var path = new List<string>();
                        path.Add("" + smep?.idShort);
                        for (int i = pars.Count - 1; i >= 0; i--)
                            path.Insert(0, "" + pars[i].idShort);

                        dynamic tuple = new ExpandoObject();
                        tuple.path = path;
                        tuple.value = "" + EvalGetSubmodelElementsProperty_EvalValue(smep);
                        if (smep.valueId != null)
                            tuple.valueId = smep.valueId;

                        res.Add(tuple);
                    }
                });

            // ok
            return res;
        }

        public void EvalGetSubmodelAllElementsProperty(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // Submodel or SME?
            if (elemids == null || elemids.Length < 1)
            {
                // send the whole Submodel
                res.values = EvalGetSubmodelElementsProperty_EvalValues(sm.submodelElements);
            }
            else
            {
                // find the right SubmodelElement
                var fse = this.FindSubmodelElement(sm, sm.submodelElements, elemids);

                if (fse?.elem is AdminShell.SubmodelElementCollection smec)
                {
                    res.values = EvalGetSubmodelElementsProperty_EvalValues(smec.value);
                }
                else if (fse?.elem is AdminShell.Property smep)
                {
                    res.value = "" + EvalGetSubmodelElementsProperty_EvalValue(smep);
                    if (smep.valueId != null)
                        res.valueId = smep.valueId;
                }
                else
                {
                    context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching Property element(s) " +
                        $"in Submodel found.");
                    return;
                }
            }

            // just send the result
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalGetSubmodelElementsFile(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // find the right SubmodelElement
            var fse = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
            var smef = fse?.elem as AdminShell.File;
            if (smef == null || smef.value == null || smef.value == "")
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching File element in Submodel found.");
                return;
            }

            // access
            var packageStream = this.Packages[0].GetLocalStreamFromPackage(smef.value);
            if (packageStream == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No file contents available in package.");
                return;
            }

            // return as FILE
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendStreamResponse(context, packageStream, Path.GetFileName(smef.value));
            packageStream.Close();
        }

        public void EvalPutSubmodelElementContents(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // de-serialize SubmodelElement
            AdminShell.SubmodelElement sme = null;
            try
            {
                sme = Newtonsoft.Json.JsonConvert.DeserializeObject<AdminShell.SubmodelElement>(context.Request.Payload, new AdminShellConverters.JsonAasxConverter("modelType", "name"));
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }

            // need id for idempotent behaviour
            if (sme.idShort == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"idShort of entity is (null); PUT cannot be performed.");
                return;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // special case: parent is Submodel itself
            var updated = false;
            if (elemids == null || elemids.Length < 1)
            {
                var existsmw = sm.FindSubmodelElementWrapper(sme.idShort);
                if (existsmw != null)
                {
                    updated = true;
                    context.Server.Logger.Debug($"Removing old SubmodelElement {sme.idShort} from Submodel {smid}.");
                    int indexOfExistingSmw = sm.submodelElements.IndexOf(existsmw);
                    sm.submodelElements.RemoveAt(indexOfExistingSmw);
                    sm.Insert(indexOfExistingSmw, sme);
                }
                else
                {
                    context.Server.Logger.Debug($"Adding new SubmodelElement {sme.idShort} to Submodel {smid}.");
                    sm.Add(sme);
                }
            }
            else
            {
                // find the right SubmodelElement
                var parent = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
                if (parent == null)
                {
                    context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching element in Submodel found.");
                    return;
                }

                if (parent.elem != null && parent.elem is AdminShell.SubmodelElementCollection)
                {
                    var parentsmc = parent.elem as AdminShell.SubmodelElementCollection;
                    var existsmw = parentsmc.FindFirstIdShort(sme.idShort);
                    if (existsmw != null)
                    {
                        updated = true;
                        context.Server.Logger.Debug($"Removing old SubmodelElement {sme.idShort} from SubmodelCollection.");
                        int indexOfExistingSmw = parentsmc.value.IndexOf(existsmw);
                        parentsmc.value.RemoveAt(indexOfExistingSmw);
                        parentsmc.Insert(indexOfExistingSmw, sme);
                    }
                    else
                    {
                        context.Server.Logger.Debug($"Adding new SubmodelElement {sme.idShort} to SubmodelCollection.");
                        parentsmc.Add(sme);
                    }
                }
                else
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Matching SubmodelElement in Submodel {smid} is not suitable to add childs.");
                    return;
                }

            }

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + (updated ? " (with updates)" : ""));
        }

        public void EvalDeleteSubmodelElementContents(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "DELETE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null || elemids == null || elemids.Length < 1)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found or no elements to delete specified.");
                return;
            }

            // OK, Submodel and Element existing
            var fse = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
            if (fse == null || fse.elem == null || fse.parent == null || fse.wrapper == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching element in Submodel found.");
                return;
            }

            // where to delete?
            var deleted = false;
            var elinfo = string.Join(".", elemids);
            if (fse.parent == sm)
            {
                context.Server.Logger.Debug($"Deleting specified SubmodelElement {elinfo} from Submodel {smid}.");
                sm.submodelElements.Remove(fse.wrapper);
                deleted = true;
            }

            if (fse.parent is AdminShell.SubmodelElementCollection)
            {
                var smc = fse.parent as AdminShell.SubmodelElementCollection;
                context.Server.Logger.Debug($"Deleting specified SubmodelElement {elinfo} from SubmodelElementCollection {smc.idShort}.");
                smc.value.Remove(fse.wrapper);
                deleted = true;
            }

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + (!deleted ? " (but nothing deleted)" : ""));
        }

        public void EvalInvokeSubmodelElementOperation(IHttpContext context, string aasid, string smid, string[] elemids)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/submodelelements", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and Submodel
            var sm = this.FindSubmodelWithinAas(aasid, smid, context.Request.QueryString, context.Request.RawUrl);

            if (sm == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no Submodel with idShort '{smid}' found.");
                return;
            }

            // find the right SubmodelElement
            var fse = this.FindSubmodelElement(sm, sm.submodelElements, elemids);
            var smep = fse?.elem as AdminShell.Operation;
            if (smep == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No matching Operation element in Submodel found.");
                return;
            }

            // make 1st expectation
            int numExpectedInputArgs = smep.inputVariable?.Count ?? 0;
            int numGivenInputArgs = 0;
            int numExpectedOutputArgs = smep.outputVariable?.Count ?? 0;
            var inputArguments = (new int[numExpectedInputArgs]).Select(x => "").ToList();
            var outputArguments = (new int[numExpectedOutputArgs]).Select(x => "my value").ToList();

            // is a payload required? Always, if at least one input argument required

            if (smep.inputVariable != null && smep.inputVariable.Count > 0)
            {
                // payload present
                if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload for Operation input argument or content type is not JSON.");
                    return;
                }

                // de-serialize SubmodelElement
                try
                {
                    // serialize
                    var input = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(context.Request.Payload);

                    // set inputs
                    if (input != null && input.Count > 0)
                    {
                        numGivenInputArgs = input.Count;
                        for (int i = 0; i < numGivenInputArgs; i++)
                            inputArguments[i] = input[i];
                    }
                }
                catch (Exception ex)
                {
                    context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                    return;
                }
            }

            // do a check
            if (numExpectedInputArgs != numGivenInputArgs)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Number of input arguments in payload does not fit expected input arguments of Operation.");
                return;
            }

            // just a test
            if (smep.HasQualifierOfType("DEMO") != null)
            {
                for (int i = 0; i < Math.Min(numExpectedInputArgs, numExpectedOutputArgs); i++)
                    outputArguments[i] = "CALC on " + inputArguments[i];
            }

            // return as little dynamic object
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, outputArguments);
        }

        public void EvalGetAllCds(IHttpContext context, string aasid)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/cds", "READ"))
                {
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                return;
            }

            // build a list of results
            var res = new List<ExpandoObject>();

            // create a new, filtered AasEnv
            // (this is expensive, but delivers us with a list of CDs which are in relation to the respective AAS)
            var copyenv = AdminShell.AdministrationShellEnv.CreateFromExistingEnv(this.Packages[findAasReturn.iPackage].AasEnv, filterForAas: new List<AdminShell.AdministrationShell>(new AdminShell.AdministrationShell[] { findAasReturn.aas }));

            // get all CDs and describe them
            foreach (var cd in copyenv.ConceptDescriptions)
            {
                // describe
                dynamic o = new ExpandoObject();
                o.idShort = cd.idShort;
                o.shortName = cd.GetDefaultShortName();
                o.identification = cd.identification;
                o.isCaseOf = cd.IsCaseOf;

                // add
                res.Add(o);
            }

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalGetCdContents(IHttpContext context, string aasid, string cdid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/cds", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and CD
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            var cd = this.FindCdWithoutAas(findAasReturn, cdid, context.Request.QueryString, context.Request.RawUrl);

            if (cd == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no ConceptDescription with id '{cdid}' found.");
                return;
            }

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, cd);
        }

        public void EvalDeleteSpecificCd(IHttpContext context, string aasid, string cdid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/cds", "DELETE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // access AAS and CD
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            var cd = this.FindCdWithoutAas(findAasReturn, cdid, context.Request.QueryString, context.Request.RawUrl);
            if (cd == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS '{aasid}' or no ConceptDescription with id '{cdid}' found.");
                return;
            }

            // delete ?!
            var deleted = false;
            if (this.Packages[findAasReturn.iPackage] != null && this.Packages[findAasReturn.iPackage].AasEnv != null && this.Packages[findAasReturn.iPackage].AasEnv.ConceptDescriptions.Contains(cd))
            {
                this.Packages[findAasReturn.iPackage].AasEnv.ConceptDescriptions.Remove(cd);
                deleted = true;
            }

            // return as JSON
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + (!deleted ? " (but nothing deleted)" : ""));
        }

        #endregion

        #region // GET + POST handles/identification

        public void EvalGetHandlesIdentification(IHttpContext context)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/handles", "READ"))
                {
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // get the list
            var res = IdRefHandleStore.FindAll<AasxHttpHandleIdentification>();

            // return this list
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalPostHandlesIdentification(IHttpContext context)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/handles", "UPDATE"))
                {
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // list of Identification
            List<AdminShell.Identification> ids = null;
            try
            {
                ids = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AdminShell.Identification>>(context.Request.Payload);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }
            if (ids == null || ids.Count < 1)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No Identification entities in payload.");
                return;
            }

            // OZ
            // Hack ASSX laden

            // turn these list into a list of Handles
            var res = new List<AasxHttpHandleIdentification>();
            foreach (var id in ids)
            {
                var h = new AasxHttpHandleIdentification(id);
                IdRefHandleStore.Add(h);
                res.Add(h);
            }

            // return this list
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        #endregion

        #region // Server profile ..

        public void EvalGetServerProfile(IHttpContext context)
        {
            dynamic res1 = new ExpandoObject();
            int index = -1;

            // check authentication
            if (false && withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (accessrights == null)
                {
                    res1.error = "You are not authorized for this operation!";
                    SendJsonResponse(context, res1);
                    return;
                }

                res1.confirm = "Authorization = " + accessrights;
            }

            // get the list
            dynamic res = new ExpandoObject();
            var capabilities = new List<ulong>(new ulong[]{
                80,81,82,10,11,12,13,15,16,20,21,30,31,40,41,42,43,50,51,52,53,54,55,56,57,58,59,60,61,70,71,72,73
            });
            res.apiversion = 1;
            res.capabilities = capabilities;

            // return this list
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public static bool withAuthentification = false;

        public static string GuestToken = null;

        public static string secretString = "Industrie4.0-Asset-Administration-Shell";

        private static RNGCryptoServiceProvider rngCsp = new RNGCryptoServiceProvider();

        public int sessionCount = 0;
        public char[] sessionUserType = new char[100];
        public static string[] sessionUserName = new string[100];
        public static RSA[] sessionUserPulicKey = new RSA[100];
        public static string[] sessionRandom = new string[100];
        public static string[] sessionChallenge = new string[100];

        public void EvalGetAuthenticateGuest(IHttpContext context)
        {
            Console.WriteLine();
            Console.WriteLine("AuthenticateGuest"); // GET

            // string with real random numbers
            Byte[] barray = new byte[100];
            rngCsp.GetBytes(barray);
            sessionRandom[sessionCount] = Convert.ToBase64String(barray);

            dynamic res = new ExpandoObject();
            var payload = new Dictionary<string, object>()
            {
                { "sessionID", sessionCount },
                { "sessionRandom", sessionRandom[sessionCount] }
            };

            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
            GuestToken = Jose.JWT.Encode(payload, enc.GetBytes(secretString), JwsAlgorithm.HS256);

            Console.WriteLine("SessionID: " + sessionCount);
            Console.WriteLine("sessionRandom: " + GuestToken);

            withAuthentification = true;

            sessionUserType[sessionCount] = 'G';
            sessionUserName[sessionCount] = "guest";
            sessionCount++;
            if (sessionCount >= 100)
            {
                Console.WriteLine("ERROR: More than 100 sessions!");
                Environment.Exit(-1);
            }

            res.token = GuestToken;

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalPostAuthenticateUser(IHttpContext context)
        {
            Console.WriteLine();
            Console.WriteLine("AuthenticateUser"); // POST User, Password

            bool userFound = false;
            bool error = false;

            dynamic res = new ExpandoObject();

            var parsed = JObject.Parse(context.Request.Payload);

            string user = null;
            string password = null;
            try
            {
                user = parsed.SelectToken("user").Value<string>();
                password = parsed.SelectToken("password").Value<string>();
            }
            catch
            {
                error = true;
            }

            if (!error)
            {
                int userCount = securityUserName.Length;

                for (int i = 0; i < userCount; i++)
                {
                    if (user == securityUserName[i] && password == securityUserPassword[i])
                    {
                        userFound = true;
                        break;
                    }
                }
            }

            if (error || !userFound)
            {
                res.error = "User not authorized!";
                SendJsonResponse(context, res);
                return;
            }

            // string with real random numbers
            Byte[] barray = new byte[100];
            rngCsp.GetBytes(barray);
            sessionRandom[sessionCount] = Convert.ToBase64String(barray);

            var payload = new Dictionary<string, object>()
            {
                { "sessionID", sessionCount },
                { "sessionRandom", sessionRandom[sessionCount] }
            };

            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
            string token = Jose.JWT.Encode(payload, enc.GetBytes(secretString), JwsAlgorithm.HS256);

            Console.WriteLine("SessionID: " + sessionCount);
            Console.WriteLine("sessionRandom: " + token);

            sessionUserType[sessionCount] = 'U';
            sessionUserName[sessionCount] = user;
            sessionCount++;
            if (sessionCount >= 100)
            {
                Console.WriteLine("ERROR: More than 100 sessions!");
                Environment.Exit(-1);
            }

            withAuthentification = true;

            res.token = token;

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalPostAuthenticateCert1(IHttpContext context)
        {
            Console.WriteLine();
            Console.WriteLine("Security 2 Server: /AuthenticateCert1"); // POST token with user

            sessionUserType[sessionCount] = ' ';
            sessionUserName[sessionCount] = "";
            sessionRandom[sessionCount] = "";
            sessionChallenge[sessionCount] = "";

            bool error = false;

            dynamic res = new ExpandoObject();
            X509Certificate2 x509 = null;
            string user = null;
            string token = null;
            RSA publicKey = null;

            try
            {
                var parsed = JObject.Parse(context.Request.Payload);
                token = parsed.SelectToken("token").Value<string>();

                var headers = Jose.JWT.Headers(token);
                string x5c = headers["x5c"].ToString();

                if (x5c != "")
                {
                    Console.WriteLine("Security 2.1a Server: x5c with certificate chain received");

                    parsed = JObject.Parse(Jose.JWT.Payload(token));
                    user = parsed.SelectToken("user").Value<string>();

                    X509Store storeCA = new X509Store("CA", StoreLocation.CurrentUser);
                    storeCA.Open(OpenFlags.ReadWrite);
                    bool valid = false;

                    string[] x5c64 = JsonConvert.DeserializeObject<string[]>(x5c);

                    X509Certificate2Collection xcc = new X509Certificate2Collection();
                    Byte[] certFileBytes = Convert.FromBase64String(x5c64[0]);
                    string fileCert = "./temp/" + user + ".cer";
                    File.WriteAllBytes(fileCert, certFileBytes);
                    Console.WriteLine("Security 2.1b Server: " + fileCert + " received");

                    x509 = new X509Certificate2(certFileBytes);
                    xcc.Add(x509);

                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine("-----BEGIN CERTIFICATE-----");
                    builder.AppendLine(
                        Convert.ToBase64String(x509.RawData, Base64FormattingOptions.InsertLineBreaks));
                    builder.AppendLine("-----END CERTIFICATE-----");
                    Console.WriteLine("Certificate: ");
                    Console.WriteLine(builder);

                    for (int i = 1; i < x5c64.Length; i++)
                    {
                        var cert = new X509Certificate2(Convert.FromBase64String(x5c64[i]));
                        Console.WriteLine("Security 2.1c Certificate in Chain: " + cert.Subject);
                        if (cert.Subject != cert.Issuer)
                        {
                            xcc.Add(cert);
                            storeCA.Add(cert);
                        }
                    }

                    X509Chain c = new X509Chain();
                    c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    valid = c.Build(x509);

                    storeCA.RemoveRange(xcc);
                    Console.WriteLine("Security 2.1d Server: Validate chain with root cert");

                    if (!valid)
                    {
                        Console.WriteLine("ERROR: Certificate " + x509.Subject + " not valid!");
                        error = true;
                    }
                }
                else
                {
                    parsed = JObject.Parse(Jose.JWT.Payload(token));
                    user = parsed.SelectToken("user").Value<string>();

                    string fileCert = "./user/" + user + ".cer";
                    if (File.Exists(fileCert))
                    {
                        x509 = new X509Certificate2(fileCert);
                        Console.WriteLine("Security 2.1a Server: " + fileCert + "exists");
                    }
                    else
                    {
                        // receive .cer and verify against root
                        string certFileBase64 = parsed.SelectToken("certFile").Value<string>();
                        Byte[] certFileBytes = Convert.FromBase64String(certFileBase64);
                        fileCert = "./temp/" + user + ".cer";
                        File.WriteAllBytes(fileCert, certFileBytes);
                        Console.WriteLine("Security 2.1b Server: " + fileCert + " received");

                        x509 = new X509Certificate2(certFileBytes);

                        // check if certifcate is valid according to root certificates
                        X509Chain chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        bool isValid = chain.Build(x509);
                        Console.WriteLine("Security 2.1c Server: Validate " + fileCert + " with root cert");

                        if (!isValid)
                        {
                            error = true;
                        }
                    }
                }
            }
            catch
            {
                Console.WriteLine("ERROR: Certificate " + user + ".cer" + " not found!");
                error = true;
            }

            if (!error)
            {
                try
                {
                    publicKey = x509.GetRSAPublicKey();

                    Jose.JWT.Decode(token, publicKey, JwsAlgorithm.RS256); // signed by user key?
                    Console.WriteLine("Security 2.2 Server: Validate signature with publicKey");
                }
                catch
                {
                    error = true;
                }
            }

            if (!error)
            {
                // user information was correctly signed

                // string with real random numbers
                Byte[] barray = new byte[100];
                rngCsp.GetBytes(barray);

                Console.WriteLine("Security 2.3 Server: Create session unique challenge by real random");
                sessionChallenge[sessionCount] = Convert.ToBase64String(barray);
            }

            if (error)
            {
                res.error = "User not authorized!";
                SendJsonResponse(context, res);
                return;
            }

            Console.WriteLine("sessionID: " + sessionCount);
            Console.WriteLine("session challenge: " + sessionChallenge[sessionCount]);

            sessionUserType[sessionCount] = 'T';
            sessionUserName[sessionCount] = user;
            sessionUserPulicKey[sessionCount] = publicKey;

            withAuthentification = true;

            res.challenge = sessionChallenge[sessionCount];

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalPostAuthenticateCert2(IHttpContext context)
        {
            Console.WriteLine();
            Console.WriteLine("Security 3 Server: /AuthenticateCert2"); // POST token with user

            sessionRandom[sessionCount] = "";

            bool error = false;

            dynamic res = new ExpandoObject();
            string token = null;
            string challenge = null;
            RSA publicKey = null;

            try
            {
                var parsed = JObject.Parse(context.Request.Payload);
                token = parsed.SelectToken("token").Value<string>();

                parsed = JObject.Parse(Jose.JWT.Payload(token));
                challenge = parsed.SelectToken("challenge").Value<string>();
            }
            catch
            {
                Console.WriteLine("ERROR: Challenge sent is not received back!");
                error = true;
            }

            if (challenge != sessionChallenge[sessionCount] || sessionChallenge[sessionCount] == null || sessionChallenge[sessionCount] == "")
            {
                error = true;
            }

            if (!error)
            {
                try
                {
                    publicKey = sessionUserPulicKey[sessionCount];

                    Jose.JWT.Decode(token, publicKey, JwsAlgorithm.RS256); // signed by user key?
                    Console.WriteLine("Security 3.1 Server: Validate challenge signature with publicKey");
                }
                catch
                {
                    error = true;
                }
            }

            if (!error)
            {
                // challenge was correctly signed

                // string with real random numbers
                Byte[] barray = new byte[100];
                rngCsp.GetBytes(barray);
                Console.WriteLine("Security 3.2 Server: Create session unique bearerToken signed by real random");
                sessionRandom[sessionCount] = Convert.ToBase64String(barray);

                var payload = new Dictionary<string, object>()
                {
                    { "sessionID", sessionCount },
                };

                try
                {
                    var enc = new System.Text.ASCIIEncoding();
                    token = Jose.JWT.Encode(payload, enc.GetBytes(sessionRandom[sessionCount]), JwsAlgorithm.HS256);
                    Console.WriteLine("Security 3.3 Server: Sign sessionID by server sessionRandom");
                }
                catch
                {
                    error = true;
                }
            }

            if (error)
            {
                res.error = "User not authorized!";
                SendJsonResponse(context, res);
                return;
            }

            Console.WriteLine("sessionID: " + sessionCount);
            Console.WriteLine("session random: " + sessionRandom[sessionCount]);
            Console.WriteLine("session bearerToken: " + token);

            sessionCount++;
            if (sessionCount >= 100)
            {
                Console.WriteLine("ERROR: More than 100 sessions!");
                Environment.Exit(-1);
            }

            withAuthentification = true;

            res.token = token;

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public bool checkAccessLevel(string currentRole, string operation, string neededRights,
            string objPath = "", string aasOrSubmodel = null, object objectAasOrSubmodel = null)
        {
            if (currentRole == null)
                currentRole = "isNotAuthenticated";

            int iRole = 0;
            while (securityRole != null && iRole < securityRole.Count && securityRole[iRole].name != null)
            {
                if (aasOrSubmodel == "aas" && securityRole[iRole].objType == "aas" &&
                    objectAasOrSubmodel != null && securityRole[iRole].objReference == objectAasOrSubmodel &&
                    securityRole[iRole].permission == neededRights)
                {
                    if ((securityRole[iRole].condition == "" && securityRole[iRole].name == currentRole) ||
                        (securityRole[iRole].condition == "not" && securityRole[iRole].name != currentRole))
                    {
                        if (securityRole[iRole].kind == "allow")
                            return true;
                        if (securityRole[iRole].kind == "deny")
                            return false;
                    }
                }
                if (securityRole[iRole].name == currentRole && securityRole[iRole].objType == "api")
                {
                    if (securityRole[iRole].apiOperation == "*" || securityRole[iRole].apiOperation == operation)
                    {
                        if (securityRole[iRole].permission == neededRights)
                        {
                            return true;
                        }
                    }
                }
                iRole++;
            }
            if (operation == "/submodelelements" && objPath != "")
            {
                // next object with rule must have allow
                // no objects below must have deny
                string deepestDeny = "";
                string deepestAllow = "";
                foreach (var role in securityRole)
                {
                    if (role.objType == "submodelElement")
                    {
                        if (role.kind == "deny")
                        {
                            if (objPath.Length >= role.objPath.Length) // deny in tree above
                            {
                                if (role.objPath == objPath.Substring(0, role.objPath.Length))
                                    deepestDeny = role.objPath;
                            }
                            if (role.objPath.Length >= objPath.Length) // deny in tree below
                            {
                                if (objPath == role.objPath.Substring(0, objPath.Length))
                                    return false;
                            }
                        }
                        if (role.kind == "allow")
                        {
                            if (objPath.Length >= role.objPath.Length) // allow in tree above
                            {
                                if (role.objPath == objPath.Substring(0, role.objPath.Length))
                                    deepestAllow = role.objPath;
                            }
                        }
                    }
                }
                if (deepestDeny.Length > deepestAllow.Length)
                    return false;
                return true;
            }

            return false;
        }

        public bool checkAccessRights(IHttpContext context, string currentRole, string operation, string neededRights,
            string objPath = "", string aasOrSubmodel = null, object objectAasOrSubmodel = null)
        {
            if (checkAccessLevel(currentRole, operation, neededRights, objPath, aasOrSubmodel, objectAasOrSubmodel))
                return true;

            if (currentRole == null)
            {
                if (AasxServer.Program.redirectServer != "")
                {
                    System.Collections.Specialized.NameValueCollection queryString = System.Web.HttpUtility.ParseQueryString(string.Empty);
                    string originalRequest = context.Request.Url.ToString();
                    queryString.Add("OriginalRequest", originalRequest);
                    Console.WriteLine("\nRedirect OriginalRequset: " + originalRequest);
                    string response = AasxServer.Program.redirectServer + "?" + "authType=" + AasxServer.Program.authType + "&" + queryString;
                    Console.WriteLine("Redirect Response: " + response + "\n");
                    SendRedirectResponse(context, response);
                    return false;
                }
            }

            dynamic res = new ExpandoObject();
            res.error = "You are not authorized for this operation!";
            context.Response.StatusCode = HttpStatusCode.Unauthorized;
            SendJsonResponse(context, res);

            return false;
        }
        public string SecurityCheck(IHttpContext context, ref int index)
        {
            bool error = false;
            string accessrights = null;

            // receive token with sessionID inside
            // check if token is signed by sessionRandom
            // read username for sessionID
            // check accessrights for username

            dynamic res = new ExpandoObject();
            int id = -1;
            string token = null;
            string random = null;
            string bearerToken = null;
            string user = null;

            index = -1; // not found

            string[] split = null;

            // Check bearer token
            string headers = context.Request.Headers.ToString();
            token = context.Request.Headers.Get("Authorization");
            if (token != null)
            {
                split = token.Split(new Char[] { ' ', '\t' });
                if (split[0] != null)
                {
                    if (split[0].ToLower() == "bearer")
                    {
                        Console.WriteLine("Received bearer token = " + split[1]);
                        bearerToken = split[1];
                    }
                }
            }
            else // check query string for bearer token
            {
                split = context.Request.Url.ToString().Split(new char[] { '?' });
                if (split != null && split.Length > 1 && split[1] != null)
                {
                    Console.WriteLine("Received query string = " + split[1]);
                    bearerToken = split[1];
                }
            }

            if (bearerToken == null)
            {
                error = true;
            }

            if (!error)
            {
                try
                {
                    var parsed2 = JObject.Parse(Jose.JWT.Payload(bearerToken));

                    string serverName = parsed2.SelectToken("serverName").Value<string>();

                    if (serverName != "") // token from Auth Server
                    {
                        X509Certificate2 cert = serverCertFind(serverName);

                        if (cert == null) return null;

                        StringBuilder builder = new StringBuilder();
                        builder.AppendLine("-----BEGIN CERTIFICATE-----");
                        builder.AppendLine(
                            Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks));
                        builder.AppendLine("-----END CERTIFICATE-----");
                        Console.WriteLine("Token Server Certificate: " + serverName);
                        Console.WriteLine(builder);

                        try
                        {
                            Jose.JWT.Decode(bearerToken, cert.GetRSAPublicKey(), JwsAlgorithm.RS256); // correctly signed by auth server cert?
                        }
                        catch
                        {
                            return null;
                        }

                        user = parsed2.SelectToken("userName").Value<string>();
                        user = user.ToLower();

                        if (securityRights != null)
                        {
                            int rightsCount = securityRights.Count;

                            for (int i = 0; i < rightsCount; i++)
                            {
                                if (securityRights[i].name.Contains("@")) // email address
                                {
                                    if (user == securityRights[i].name)
                                    {
                                        // accessrights = securityRightsValue[i];
                                        accessrights = securityRights[i].role;
                                        return accessrights;
                                    }
                                }
                            }
                            for (int i = 0; i < rightsCount; i++)
                            {
                                if (!securityRights[i].name.Contains("@")) // domain name only
                                {
                                    string[] splitUser = user.Split('@');
                                    if (splitUser[1] == securityRights[i].name)
                                    {
                                        // accessrights = securityRightsValue[i];
                                        accessrights = securityRights[i].role;
                                        return accessrights;
                                    }
                                }
                            }
                        }

                        return accessrights;
                    }
                    else
                    {
                        id = Convert.ToInt32(parsed2.SelectToken("sessionID").Value<string>());

                        random = sessionRandom[id];
                        user = sessionUserName[id];

                        if (random == null || random == "" || user == null || user == "")
                        {
                            error = true;
                        }
                    }
                }
                catch
                {
                    error = true;
                }
            }

            if (!error)
            {
                try
                {
                    string payload = null;

                    switch (sessionUserType[id])
                    {
                        case 'G':
                        case 'U':
                        case 'T':
                            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                            payload = Jose.JWT.Decode(bearerToken, enc.GetBytes(random), JwsAlgorithm.HS256); // correctly signed by session token?
                            break;
                    }
                }
                catch
                {
                    error = true;
                }
            }

            /*
            if (!error)
            {
                switch (sessionUserType[id])
                {
                    case 'G':
                        accessrights = "GUEST";
                        break;
                    case 'U':
                    case 'T':
                        if (securityRightsName != null)
                        {
                            int rightsCount = securityRightsName.Length;

                            for (int i = 0; i < rightsCount; i++)
                            {
                                if (user == securityRightsName[i])
                                {
                                    accessrights = securityRightsValue[i];
                                    break;
                                }
                            }
                        }
                        break;
                }
            }
            */

            if (!error)
            {
                index = id;
            }
            return accessrights;
        }

        public void EvalGetListAAS(IHttpContext context)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            Console.WriteLine("Security 4 Server: /server/listaas");

            string accessrights = SecurityCheck(context, ref index);

            // get the list
            var aaslist = new List<string>();

            int aascount = AasxServer.Program.env.Length;

            for (int i = 0; i < aascount; i++)
            {
                if (AasxServer.Program.env[i] != null)
                {
                    var aas = AasxServer.Program.env[i].AasEnv.AdministrationShells[0];
                    string idshort = aas.idShort;
                    string aasRights = "NONE";
                    if (securityRightsAAS != null && securityRightsAAS.Count != 0)
                        securityRightsAAS.TryGetValue(idshort, out aasRights);
                    // aasRights = securityRightsAAS[idshort];

                    bool addEntry = false;
                    if (!withAuthentification || checkAccessLevel(accessrights, "/server/listaas", "READ", "", "aas", aas))
                    {
                        addEntry = true;
                    }

                    if (addEntry)
                    {
                        aaslist.Add(i.ToString() + " : "
                            + idshort + " : "
                            + aas.identification + " : "
                            + AasxServer.Program.envFileName[i]);
                    }
                }
            }

            res.aaslist = aaslist;

            // return this list
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }

        public void EvalAssetId(IHttpContext context, int assetId)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            Console.WriteLine("Test Asset ID");

            string headers = context.Request.Headers.ToString();
            string token = context.Request.Headers.Get("accept");
            if (token != null)
            {
                if (token == "application/aas")
                {
                    Console.WriteLine("Received Accept header = " + token);
                    // context.Response.ContentType = ContentType.JSON;
                    context.Response.AddHeader("Content-Type", "application/aas");
                    res.client = "I40 IT client";
                    res.assetID = assetId;
                    res.humanEndpoint = "https://admin-shell-io.com:5001";
                    res.restEndpoint = "http://" + AasxServer.Program.hostPort;

                    var settings = new JsonSerializerSettings();
                    // if (contractResolver != null)
                    //    settings.ContractResolver = contractResolver;
                    var json = JsonConvert.SerializeObject(res, Formatting.Indented, settings);
                    var buffer = context.Request.ContentEncoding.GetBytes(json);
                    var length = buffer.Length;

                    context.Response.ContentEncoding = Encoding.UTF8;
                    context.Response.ContentLength64 = length;
                    context.Response.StatusCode = HttpStatusCode.Ok;
                    context.Response.SendResponse(buffer);

                    return;
                }
            }

            // SendJsonResponse(context, res);
            SendRedirectResponse(context, "https://admin-shell-io.com:5001");
        }

        public void EvalGetAASX(IHttpContext context, int fileIndex)
        {
            dynamic res = new ExpandoObject();
            int index = -1;
            string accessrights = null;

            // check authentication
            if (withAuthentification)
            {
                accessrights = SecurityCheck(context, ref index);

                /*
                if (accessrights == null)
                {
                    res.error = "You are not authorized for this operation!";
                    SendJsonResponse(context, res);
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
                */

                var aas = Program.env[fileIndex].AasEnv.AdministrationShells[0];
                if (!checkAccessRights(context, accessrights, "/aasx", "READ", "", "aas", aas))
                {
                    return;
                }

                /*
                string idshort = AasxServer.Program.env[fileIndex].AasEnv.AdministrationShells[0].idShort;
                string aasRights = "NONE";
                if (securityRightsAAS.Count != 0)
                    aasRights = securityRightsAAS[idshort];
                if (!checkAccessRights(context, accessrights, aasRights))
                {
                    return;
                }
                */
            }

            // save actual data as file

            lock (Program.changeAasxFile)
            {
                string fname = "./temp/" + Path.GetFileName(Program.envFileName[fileIndex]);
                Program.env[fileIndex].SaveAs(fname);

                // return as FILE
                FileStream packageStream = File.OpenRead(fname);
                context.Response.StatusCode = HttpStatusCode.Ok;
                SendStreamResponse(context, packageStream,
                    Path.GetFileName(AasxServer.Program.envFileName[fileIndex]));
                packageStream.Close();

                // Reload
                // Program.env[fileIndex] = new AdminShellPackageEnv(fname);
            }
        }

        public void EvalGetAASX2(IHttpContext context, int fileIndex)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            string accessrights = null;
            if (withAuthentification)
            {
                accessrights = SecurityCheck(context, ref index);

                /*
                if (accessrights == null)
                {
                    res.error = "You are not authorized for this operation!";
                    SendJsonResponse(context, res);
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
                */
            }
            else
            {
                accessrights = "READ";
            }

            Console.WriteLine("Security 5 Server: /server/getaasx2/" + fileIndex);

            // check authentication
            /*
            if (!withAuthentification)
            {
                res.error = "You are not authorized for this operation!";
                SendJsonResponse(context, res);
                return;
            }
            string accessrights = SecurityCheck(context, ref index);
            */

            Console.WriteLine("Security 5.1 Server: Check bearer token and access rights");
            Console.WriteLine("Security 5.2 Server: Validate that bearer token is signed by session unique random");

            if (!checkAccessRights(context, accessrights, "/aasx", "READ"))
            {
                return;
            }

            res.confirm = "Authorization = " + accessrights;

            Byte[] binaryFile = File.ReadAllBytes(AasxServer.Program.envFileName[fileIndex]);
            string binaryBase64 = Convert.ToBase64String(binaryFile);

            string payload = "{ \"file\" : \" " + binaryBase64 + " \" }";

            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
            string fileToken = Jose.JWT.Encode(payload, enc.GetBytes(secretString), JwsAlgorithm.HS256);

            res.fileName = Path.GetFileName(AasxServer.Program.envFileName[fileIndex]);
            res.fileData = fileToken;

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendJsonResponse(context, res);
        }
        #endregion

        public void EvalGetFile(IHttpContext context, int envIndex, string filePath)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (false && withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/aasx", "READ"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            context.Response.StatusCode = HttpStatusCode.Ok;
            SendStreamResponse(context, Program.env[envIndex].GetLocalStreamFromPackage(filePath), Path.GetFileName(filePath));
        }

        public static string[] securityUserName = null;
        public static string[] securityUserPassword = null;

        public static string[] serverCertfileNames = null;
        public static X509Certificate2[] serverCerts = null;

        public static Dictionary<string, string> securityRightsAAS = null;

        public class securityRightsClass
        {
            public string name = null;
            public string value = null;
            public string role = null;
        }
        public static List<securityRightsClass> securityRights = null;

        public class securityRoleClass
        {
            public string condition = null;
            public string name = null;
            public string objType = null;
            public string apiOperation = null;
            public object objReference = null;
            public string objPath = "";
            public string permission = null;
            public string kind = null;
            public securityRoleClass() { }
        }
        public static List<securityRoleClass> securityRole = null;

        public static void securityInit()
        {
            withAuthentification = !Program.noSecurity;

            securityRole = new List<securityRoleClass>();

            int aascount = AasxServer.Program.env.Length;

            for (int i = 0; i < aascount; i++)
            {
                var env = AasxServer.Program.env[i];
                if (env != null)
                {
                    var aas = env.AasEnv.AdministrationShells[0];
                    if (aas.submodelRefs != null && aas.submodelRefs.Count > 0)
                    {
                        foreach (var smr in aas.submodelRefs)
                        {
                            var sm = env.AasEnv.FindSubmodel(smr);
                            if (sm != null && sm.idShort != null)
                            {
                                if (!sm.idShort.ToLower().Contains("Security"))
                                {
                                    sm.SetAllParents();
                                }
                            }
                        }

                        foreach (var smr in aas.submodelRefs)
                        {
                            var sm = env.AasEnv.FindSubmodel(smr);
                            if (sm != null && sm.idShort != null)
                            {
                                if (sm.idShort == "SecuritySettingsForServer")
                                {
                                    int countSme = sm.submodelElements.Count;
                                    for (int iSme = 0; iSme < countSme; iSme++)
                                    {
                                        var sme = sm.submodelElements[iSme].submodelElement;
                                        var smec = sme as AdminShell.SubmodelElementCollection;
                                        int countSmec = smec.value.Count;
                                        switch (smec.idShort)
                                        {
                                            case "authenticationServer":
                                                for (int iSmec = 0; iSmec < countSmec; iSmec++)
                                                {
                                                    var sme2 = smec.value[iSmec].submodelElement;
                                                    switch (sme2.idShort)
                                                    {
                                                        case "endpoint":
                                                            var p2 = sme2 as AdminShell.Property;
                                                            AasxServer.Program.redirectServer = p2.value;
                                                            break;
                                                        case "publicCertificate":
                                                            var f = sme2 as AdminShell.File;
                                                            serverCertfileNames = new string[1];
                                                            serverCerts = new X509Certificate2[1];
                                                            var s = AasxServer.Program.env[i].GetLocalStreamFromPackage(f.value);
                                                            if (s != null)
                                                            {
                                                                using (var m = new System.IO.MemoryStream())
                                                                {
                                                                    s.CopyTo(m);
                                                                    var b = m.GetBuffer();
                                                                    serverCerts[0] = new X509Certificate2(b);
                                                                    string[] split = f.value.Split('/');
                                                                    serverCertfileNames[0] = split[3];
                                                                    Console.WriteLine("Loaded auth server certifcate: " + serverCertfileNames[0]);
                                                                }
                                                            }
                                                            break;
                                                    }
                                                }
                                                break;
                                            case "roleMapping":
                                                securityRights = new List<securityRightsClass>();

                                                for (int iSmec = 0; iSmec < countSmec; iSmec++)
                                                {
                                                    var smec2 = smec.value[iSmec].submodelElement as AdminShell.SubmodelElementCollection;
                                                    int countSmec2 = smec2.value.Count;
                                                    List<string> subjects = new List<string>();

                                                    for (int iSmec2 = 0; iSmec2 < countSmec2; iSmec2++)
                                                    {
                                                        var smec3 = smec2.value[iSmec2].submodelElement as AdminShell.SubmodelElementCollection;
                                                        int countSmec3 = smec3.value.Count;

                                                        switch (smec3.idShort)
                                                        {
                                                            case "subjects":
                                                                for (int iSmec3 = 0; iSmec3 < countSmec3; iSmec3++)
                                                                {
                                                                    var p = smec3.value[iSmec3].submodelElement as AdminShell.Property;
                                                                    switch (p.idShort)
                                                                    {
                                                                        case "emailDomain":
                                                                        case "email":
                                                                            subjects.Add(p.value);
                                                                            break;
                                                                        default:
                                                                            subjects.Add(p.idShort);
                                                                            break;

                                                                    }
                                                                }
                                                                break;
                                                            case "roles":
                                                                for (int iSmec3 = 0; iSmec3 < countSmec3; iSmec3++)
                                                                {
                                                                    var p = smec3.value[iSmec3].submodelElement as AdminShell.Property;
                                                                    foreach (var s in subjects)
                                                                    {
                                                                        securityRightsClass sr = new securityRightsClass();
                                                                        sr.name = s;
                                                                        sr.role = p.idShort;
                                                                        securityRights.Add(sr);
                                                                    }
                                                                }
                                                                break;
                                                        }
                                                    }
                                                }
                                                break;
                                        }
                                    }
                                }
                                if (sm.idShort == "SecurityMetaModelForServer" || sm.idShort == "SecurityMetaModelForAAS")
                                {
                                    var smc1 = sm.submodelElements.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("accessControlPolicyPoints");
                                    var smc2 = smc1?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("policyAdministrationPoint");
                                    var smc3 = smc2?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("localAccessControl");
                                    var smc4 = smc3?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("accessPermissionRules");
                                    if (smc4 == null) continue;

                                    int countSme = smc4.value.Count;
                                    for (int iSme = 0; iSme < countSme; iSme++)
                                    {
                                        var sme = smc4.value[iSme].submodelElement; // actual rule
                                        var smc5 = sme as AdminShell.SubmodelElementCollection;
                                        var smc6 = smc5?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("targetSubjectAttributes");
                                        List<AdminShell.Property> role = new List<AdminShellV20.Property>();
                                        int iRole = 0;
                                        while (smc6?.value.Count > iRole)
                                        {
                                            if (smc6?.value[iRole].submodelElement is AdminShell.Property rp)
                                            {
                                                role.Add(rp);
                                                iRole++;
                                            }
                                        }
                                        smc6 = smc5?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("permissionsPerObject");
                                        var smc7 = smc6?.value[0].submodelElement as AdminShell.SubmodelElementCollection;
                                        var objProp = smc7?.value.FindFirstIdShortAs<AdminShell.Property>("object");
                                        var objRef = smc7?.value.FindFirstIdShortAs<AdminShell.ReferenceElement>("object");
                                        object aasObject = null;
                                        if (objRef != null)
                                        {
                                            aasObject = env.AasEnv.FindReferableByReference(objRef.value);
                                        }
                                        var smc8 = smc7?.value.FindFirstIdShortAs<AdminShell.SubmodelElementCollection>("permission");

                                        int countSmc8 = smc8.value.Count;
                                        List<string> listPermission = new List<string>();
                                        AdminShell.Property kind = null;
                                        for (int iSmc8 = 0; iSmc8 < countSmc8; iSmc8++)
                                        {
                                            var sme9 = smc8.value[iSmc8].submodelElement;
                                            if (sme9 is AdminShell.Property)
                                                kind = sme9 as AdminShell.Property;
                                            if (sme9 is AdminShell.ReferenceElement)
                                            {
                                                var refer = sme9 as AdminShell.ReferenceElement;
                                                var permission = env.AasEnv.FindReferableByReference(refer.value);
                                                if (!(permission is AdminShell.Property))
                                                    continue;
                                                var p = permission as AdminShell.Property;
                                                listPermission.Add(p.idShort);
                                            }
                                        }

                                        string[] split = null;
                                        foreach (var l in listPermission)
                                        {
                                            foreach (var r in role)
                                            {
                                                securityRoleClass src = new securityRoleClass();
                                                if (r.idShort.Contains(":"))
                                                {
                                                    split = r.idShort.Split(':');
                                                    src.condition = split[0].ToLower();
                                                    src.name = split[1];
                                                }
                                                else
                                                {
                                                    src.condition = "";
                                                    src.name = r.idShort;
                                                }
                                                if (objProp != null)
                                                {
                                                    string value = objProp.value.ToLower();
                                                    src.objType = value;
                                                    if (value.Contains("api"))
                                                    {
                                                        split = value.Split(':');
                                                        if (split[0] == "api")
                                                        {
                                                            src.objType = split[0];
                                                            src.apiOperation = split[1];
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    if (aasObject != null)
                                                    {
                                                        src.objReference = aasObject;
                                                        if (aasObject is AdminShell.AdministrationShell)
                                                            src.objType = "aas";
                                                        if (aasObject is AdminShell.Submodel)
                                                            src.objType = "sm";
                                                        if (aasObject is AdminShell.SubmodelElement smep)
                                                        {
                                                            AdminShell.Referable rp = smep;
                                                            src.objType = "submodelElement";
                                                            string path = rp.idShort;
                                                            while (rp.parent != null)
                                                            {
                                                                rp = rp.parent;
                                                                path = rp.idShort + "." + path;
                                                            }
                                                            src.objPath = path;
                                                        }
                                                    }
                                                }
                                                src.permission = l.ToUpper();
                                                if (kind != null)
                                                    src.kind = kind.value.ToLower();
                                                securityRole.Add(src);
                                            }
                                        }
                                        continue;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public static void serverCertsInit()
        {
            return;

            if (Directory.Exists("./authservercerts"))
            {
                serverCertfileNames = Directory.GetFiles("./authservercerts", "*.cer");

                serverCerts = new X509Certificate2[serverCertfileNames.Length];

                for (int i = 0; i < serverCertfileNames.Length; i++)
                {
                    serverCerts[i] = new X509Certificate2(serverCertfileNames[i]);
                    Console.WriteLine("Loaded auth server certifcate: " + Path.GetFileName(serverCertfileNames[i]));
                }
            }
        }

        public static X509Certificate2 serverCertFind(string authServerName)
        {
            if (serverCertfileNames != null)
            {
                for (int i = 0; i < serverCertfileNames.Length; i++)
                {
                    if (Path.GetFileName(serverCertfileNames[i]) == authServerName + ".cer")
                    {
                        return serverCerts[i];
                    }
                }
            }

            return null;
        }

        #region // Concept Descriptions

        public void EvalPutCd(IHttpContext context, string aasid)
        {
            dynamic res = new ExpandoObject();
            int index = -1;

            // check authentication
            if (withAuthentification)
            {
                string accessrights = SecurityCheck(context, ref index);

                if (!checkAccessRights(context, accessrights, "/cds", "UPDATE"))
                {
                    return;
                }

                res.confirm = "Authorization = " + accessrights;
            }

            // first check
            if (context.Request.Payload == null || context.Request.ContentType != ContentType.JSON)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"No payload or content type is not JSON.");
                return;
            }

            // access the AAS
            var findAasReturn = this.FindAAS(aasid, context.Request.QueryString, context.Request.RawUrl);
            if (findAasReturn.aas == null)
            {
                context.Response.SendResponse(HttpStatusCode.NotFound, $"No AAS with idShort '{aasid}' found.");
                return;
            }

            // de-serialize CD
            AdminShell.ConceptDescription cd = null;
            try
            {
                cd = Newtonsoft.Json.JsonConvert.DeserializeObject<AdminShell.ConceptDescription>(context.Request.Payload);
            }
            catch (Exception ex)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Cannot deserialize payload: {ex.Message}.");
                return;
            }

            // need id for idempotent behaviour
            if (cd.identification == null)
            {
                context.Response.SendResponse(HttpStatusCode.BadRequest, $"Identification of entity is (null); PUT cannot be performed.");
                return;
            }

            // datastructure update
            if (this.Packages[findAasReturn.iPackage] == null || this.Packages[findAasReturn.iPackage].AasEnv == null || this.Packages[findAasReturn.iPackage].AasEnv.Assets == null)
            {
                context.Response.SendResponse(HttpStatusCode.InternalServerError, $"Error accessing internal data structures.");
                return;
            }

            // add Submodel
            context.Server.Logger.Debug($"Adding ConceptDescription with idShort {cd.idShort ?? "--"} and id {cd.identification?.ToString() ?? "--"}");
            var existingCd = this.Packages[findAasReturn.iPackage].AasEnv.FindConceptDescription(cd.identification);
            if (existingCd != null)
                this.Packages[findAasReturn.iPackage].AasEnv.ConceptDescriptions.Remove(existingCd);
            this.Packages[findAasReturn.iPackage].AasEnv.ConceptDescriptions.Add(cd);

            // simple OK
            context.Response.StatusCode = HttpStatusCode.Ok;
            SendTextResponse(context, "OK" + ((existingCd != null) ? " (updated)" : " (new)"));
        }

        #endregion
    }
}
