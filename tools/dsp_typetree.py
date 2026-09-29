"""Normalize observed TypeTreeGeneratorAPI 0.0.10 layout issues without dropping fields."""
from UnityPy.helpers.TypeTreeNode import TypeTreeNode


def normalize_nodes(root):
    nodes = root.to_dict_list()
    for i, node in enumerate(nodes):
        # Rebuild fresh nodes: native parsing caches are not invalidated by
        # mutating m_Type, and from_list appends children itself.
        node.pop('m_Children', None)
        if node['m_Level'] == 1 and node['m_Name'] == 'm_Enabled' and node['m_Type'] == 'UInt8':
            node['m_MetaFlag'] = (node.get('m_MetaFlag') or 0) | 0x4000
        # A scalar string's Array contains chars, a string[] contains strings.
        if (node['m_Type'] == 'string' and i + 3 < len(nodes)
                and nodes[i+1]['m_Type'] == 'Array'
                and nodes[i+3]['m_Type'] == 'string'
                and nodes[i+3]['m_Level'] == node['m_Level'] + 2):
            node['m_Type'] = 'vector'
    return TypeTreeNode.from_list(nodes)
