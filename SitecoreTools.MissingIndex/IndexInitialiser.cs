using Sitecore.Abstractions;
using Sitecore.Configuration;
using Sitecore.ContentSearch;
using Sitecore.ContentSearch.SolrProvider;
using Sitecore.ContentSearch.SolrProvider.SolrNetIntegration;
using Sitecore.Diagnostics;
using Sitecore.Exceptions;
using Sitecore.Pipelines;
using Sitecore.Xml;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace SitecoreTools.MissingIndex
{
    public class IndexInitialiser
    {
        IFactory _factory = null;

        public void Process(PipelineArgs args)
        {
            if (IntegrationHelper.IsSolrConfigured())
            {
                IntegrationHelper.ReportDoubleSolrConfigurationAttempt(GetType());
                return;
            }
            if (SolrContentSearchManager.ServiceAddress.ToLower().Contains("https"))
            {
                IgnoreBadCertificates();
            }
            Initialize();
        }

        private void IgnoreBadCertificates()
        {
            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(AcceptAllCertifications);
        }

        private bool AcceptAllCertifications(object sender, X509Certificate certification, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        public void Initialize()
        {
            _factory = ContentSearchManager.Locator.GetInstance<IFactory>();
            var configNode = _factory.GetConfigNode("contentSearch/configuration", true);
            CreateObject(configNode, null, true);
        }

        public object CreateObject(XmlNode configNode, string[] parameters, bool assert)
        {
            Assert.ArgumentNotNull(configNode, "configNode");
            return CreateObject(configNode, parameters, assert, null);
        }

        public object CreateObject(XmlNode configNode, string[] parameters, bool assert, IFactoryHelper helper)
        {
            Assert.ArgumentNotNull(configNode, "configNode");
            object obj = CreateFromTypeName(configNode, parameters, assert);

            if (obj == null)
            {
                return GetStringValue(configNode, parameters);
            }

            AssignProperties(configNode, parameters, obj, assert, false, helper);

            return obj;
        }

        private object CreateFromTypeName(XmlNode configNode, string[] parameters, bool assert)
        {
            Assert.ArgumentNotNull(configNode, "configNode");
            Type type = _factory.CreateType(configNode, parameters, assert);
            if (type == null)
            {
                return null;
            }
            object[] constructorParameters = this.GetConstructorParameters(configNode, parameters, assert);
            object obj = Sitecore.Reflection.ReflectionUtil.CreateObject(type, constructorParameters);
            if (assert && obj == null)
            {
                string str = string.Concat("Could not create instance of type: ", type.FullName, ".");
                if (Sitecore.Reflection.ReflectionUtil.GetConstructorInfo(type, constructorParameters) == null)
                {
                    str = string.Concat(str, " No matching constructor was found.");
                }
                throw new ConfigurationException(str);
            }
            return obj;
        }

        private object[] GetConstructorParameters(XmlNode configNode, string[] parameters, bool assert)
        {
            Assert.ArgumentNotNull(configNode, "configNode");
            XmlNodeList xmlNodeLists = configNode.SelectNodes("param");
            object[] innerObject = new object[xmlNodeLists.Count];
            for (int i = 0; i < xmlNodeLists.Count; i++)
            {
                innerObject[i] = GetInnerObject(xmlNodeLists[i], parameters, assert);
            }
            return innerObject;
        }

        private void AssignProperties(XmlNode configNode, string[] parameters, object obj, bool assert, bool deferred, IFactoryHelper helper)
        {
            Assert.ArgumentNotNull(configNode, "configNode");
            XmlNodeList childNodes = configNode.ChildNodes;
            ArrayList arrayLists = new ArrayList();
            foreach (XmlNode childNode in childNodes)
            {
                if (!IsPropertyNode(childNode, parameters, deferred))
                {
                    continue;
                }
                arrayLists.Add(childNode);
            }
            if (arrayLists.Count > 0)
            {
                List<object> objs = new List<object>(arrayLists.Count * 2);
                foreach (XmlNode arrayList in arrayLists)
                {
                    if (helper != null && helper.SetProperty(arrayList, obj))
                    {
                        continue;
                    }
                    string name = arrayList.Name;
                    object innerObject = GetInnerObject(arrayList, parameters, assert);
                    if (innerObject == null)
                    {
                        continue;
                    }
                    objs.Add(name);
                    objs.Add(innerObject);
                }
                AssignProperties(obj, objs.ToArray());
            }
        }

        private void AssignProperties(object obj, object[] properties)
        {
            object[] objArray;
            if (properties != null)
            {
                for (int i = 0; i < (int)properties.Length - 1; i += 2)
                {
                    string str = properties[i] as string;
                    object obj1 = properties[i + 1];
                    Error.AssertString(str, "propertyName", false);
                    if (obj1 is ObjectList)
                    {
                        ObjectList objectList = obj1 as ObjectList;
                        ArrayList list = objectList.List;
                        for (int j = 0; j < list.Count - 1; j += 2)
                        {
                            string item = list[j] as string;
                            object item1 = list[j + 1];
                            if (objectList.AddMethod.Length > 0)
                            {
                                objArray = (string.IsNullOrEmpty(item) ? new object[] { item1 } : new object[] { item, item1 });
                                Assert.IsNotNull(obj, "object");
                                MethodInfo method = Sitecore.Reflection.ReflectionUtil.GetMethod(obj.GetType(), objectList.AddMethod, true, true, true, objArray);

                                if (method == null)
                                {
                                    throw new Sitecore.Exceptions.RequiredObjectIsNullException($"Could not add index {item1}. Check the config for this index and that it exists in Solr.");
                                }
                            }
                        }
                    }
                }
            }
        }

        private bool IsPropertyNode(XmlNode node, string[] parameters, bool deferred)
        {
            Assert.ArgumentNotNull(node, "node");
            if (node.NodeType != XmlNodeType.Element)
            {
                return false;
            }
            if (node.Name == "param")
            {
                return false;
            }
            if (_factory.GetAttribute("hint", node, parameters) == "skip")
            {
                return false;
            }
            if (deferred != (_factory.GetAttribute("hint", node, parameters) == "defer"))
            {
                return false;
            }
            return true;
        }

        private object GetInnerObject(XmlNode paramNode, string[] parameters, bool assert)
        {
            Lucene.Net.Util.Version version;
            object obj;
            Assert.ArgumentNotNull(paramNode, "paramNode");
            if (IsObjectNode(paramNode))
            {
                return _factory.CreateObject(paramNode, parameters, assert);
            }
            string attribute = _factory.GetAttribute("hint", paramNode, parameters);
            if (attribute.StartsWith("call:", StringComparison.InvariantCulture))
            {
                ObjectList objectList = new ObjectList(attribute.Substring(5));
                objectList.Add(string.Empty, paramNode);
                return objectList;
            }
            bool flag = (attribute.StartsWith("list:", StringComparison.InvariantCulture) || attribute == "list" ? true : attribute == "dictionary");
            bool flag1 = attribute.StartsWith("raw:", StringComparison.InvariantCulture);
            string str = Sitecore.StringUtil.Mid(attribute, (flag1 ? 4 : 5));
            XmlNodeList xmlNodeLists = paramNode.SelectNodes("node()");
            if (xmlNodeLists.Count <= 0)
            {
                if (flag | flag1)
                {
                    return new ObjectList(str);
                }
                return GetStringValue(paramNode, parameters);
            }
            if (attribute == "setting")
            {
                return null;
            }
            bool flag2 = (attribute.StartsWith("version:", StringComparison.InvariantCulture) ? true : attribute == "version");
            if (!flag && !flag1 && !flag2)
            {
                return _factory.CreateObject(xmlNodeLists[0], parameters, assert);
            }
            if (flag2)
            {
                Enum.TryParse<Lucene.Net.Util.Version>(paramNode.InnerText, true, out version);
                return version;
            }
            ObjectList objectList1 = new ObjectList(str);
            foreach (XmlNode xmlNodes in xmlNodeLists)
            {
                if (xmlNodes.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (flag1)
                {
                    ReplaceVariables(xmlNodes, parameters);
                }
                string attribute1 = _factory.GetAttribute("key", xmlNodes, parameters);
                if (flag1)
                {
                    obj = xmlNodes;
                }
                else
                {
                    obj = _factory.CreateObject(xmlNodes, parameters, assert);
                }
                object obj1 = obj;
                if (obj1 == null)
                {
                    continue;
                }
                objectList1.Add(attribute1, obj1);
            }
            return objectList1;
        }

        private bool IsObjectNode(XmlNode node)
        {
            Assert.ArgumentNotNull(node, "node");
            if (node.NodeType != XmlNodeType.Element)
            {
                return false;
            }
            if (XmlUtil.HasAttribute("ref", node) || XmlUtil.HasAttribute("type", node) || XmlUtil.HasAttribute("path", node))
            {
                return true;
            }
            return XmlUtil.HasAttribute("connectionStringName", node);
        }

        private string GetStringValue(XmlNode node, string[] parameters)
        {
            Assert.ArgumentNotNull(node, "node");
            XmlNode ownerElement = node;
            if (ownerElement.NodeType == XmlNodeType.Attribute)
            {
                XmlAttribute xmlAttribute = ownerElement as XmlAttribute;
                if (xmlAttribute != null)
                {
                    ownerElement = xmlAttribute.OwnerElement;
                }
            }
            if (ownerElement.NodeType != XmlNodeType.Element)
            {
                ownerElement = ownerElement.ParentNode;
            }
            return ReplaceVariables(node.InnerText, ownerElement, parameters);
        }

        internal static string ReplaceVariables(string value, XmlNode node, string[] parameters)
        {
            Assert.ArgumentNotNull(value, "value");
            Assert.ArgumentNotNull(node, "node");
            node = node.ParentNode;
            while (node != null && node.NodeType == XmlNodeType.Element && value.IndexOf("$(", StringComparison.InvariantCulture) >= 0)
            {
                foreach (XmlAttribute attribute in node.Attributes)
                {
                    string str = string.Concat("$(", attribute.LocalName, ")");
                    value = value.Replace(str, attribute.Value);
                }
                value = value.Replace("$(name)", node.LocalName);
                node = node.ParentNode;
            }
            if (parameters != null)
            {
                for (int i = 0; i < (int)parameters.Length; i++)
                {
                    value = value.Replace(string.Concat("$(", i, ")"), parameters[i]);
                }
            }
            return value;
        }

        internal static void ReplaceVariables(XmlNode node, string[] parameters)
        {
            Assert.ArgumentNotNull(node, "node");
            foreach (XmlAttribute attribute in node.Attributes)
            {
                attribute.Value = ReplaceVariables(attribute.Value, node, parameters);
            }
        }
    }
}
