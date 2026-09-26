/* eslint-disable */
import * as types from './graphql';



/**
 * Map of all GraphQL operations in the project.
 *
 * This map has several performance disadvantages:
 * 1. It is not tree-shakeable, so it will include all operations in the project.
 * 2. It is not minifiable, so the string of a GraphQL query will be multiple times inside the bundle.
 * 3. It does not support dead code elimination, so it will add unused operations.
 *
 * Therefore it is highly recommended to use the babel or swc plugin for production.
 * Learn more about it here: https://the-guild.dev/graphql/codegen/plugins/presets/preset-client#reducing-bundle-size
 */
type Documents = {
    "\n  query Viewer {\n    viewer {\n      email\n      permissions\n    }\n  }\n": typeof types.ViewerDocument,
    "\n  query Containers($after: String) {\n    containers(first: 100, after: $after) {\n      totalCount\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        code\n      }\n    }\n  }\n": typeof types.ContainersDocument,
    "\n  query Temperatures($containerId: UUID!, $after: String) {\n    temperatures(containerId: $containerId, first: 100, after: $after) {\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        timestampUtc\n        celsius\n      }\n    }\n  }\n": typeof types.TemperaturesDocument,
    "\n  query Documents {\n    documents(first: 100) {\n      nodes {\n        id\n        fileName\n        contentType\n        sizeBytes\n        status\n        createdAtUtc\n        downloadUrl\n      }\n    }\n  }\n": typeof types.DocumentsDocument,
    "\n  mutation RequestDocumentUpload($input: RequestDocumentUploadInput!) {\n    requestDocumentUpload(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n        document {\n          id\n        }\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": typeof types.RequestDocumentUploadDocument,
    "\n  mutation ConfirmDocumentUpload($input: ConfirmDocumentUploadInput!) {\n    confirmDocumentUpload(input: $input) {\n      document {\n        id\n        status\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": typeof types.ConfirmDocumentUploadDocument,
    "\n  mutation RenameDocument($input: RenameDocumentInput!) {\n    renameDocument(input: $input) {\n      document {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": typeof types.RenameDocumentDocument,
    "\n  mutation ReplaceDocumentContent($input: ReplaceDocumentContentInput!) {\n    replaceDocumentContent(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": typeof types.ReplaceDocumentContentDocument,
    "\n  mutation DeleteDocument($input: DeleteDocumentInput!) {\n    deleteDocument(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": typeof types.DeleteDocumentDocument,
    "\n  query Products {\n    products(first: 100, order: [{ name: ASC }]) {\n      totalCount\n      nodes {\n        id\n        name\n        sku\n        price\n        isArchived\n        rowVersion\n        updatedAtUtc\n        updatedBy\n      }\n    }\n  }\n": typeof types.ProductsDocument,
    "\n  mutation CreateProduct($input: CreateProductInput!) {\n    createProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": typeof types.CreateProductDocument,
    "\n  mutation UpdateProduct($input: UpdateProductInput!) {\n    updateProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": typeof types.UpdateProductDocument,
    "\n  mutation DeleteProduct($input: DeleteProductInput!) {\n    deleteProduct(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": typeof types.DeleteProductDocument,
    "\n  query ProductPermissions {\n    viewer {\n      permissions\n    }\n  }\n": typeof types.ProductPermissionsDocument,
    "\n  query ProductAudit($productId: UUID!) {\n    productAudit(productId: $productId, first: 50) {\n      nodes {\n        id\n        change\n        name\n        price\n        occurredAtUtc\n      }\n    }\n  }\n": typeof types.ProductAuditDocument,
    "\n  query Queues {\n    queues {\n      name\n      messages\n      consumers\n    }\n  }\n": typeof types.QueuesDocument,
    "\n  mutation PurgeQueue($input: PurgeQueueInput!) {\n    purgeQueue(input: $input) {\n      unsignedInt\n    }\n  }\n": typeof types.PurgeQueueDocument,
};
const documents: Documents = {
    "\n  query Viewer {\n    viewer {\n      email\n      permissions\n    }\n  }\n": types.ViewerDocument,
    "\n  query Containers($after: String) {\n    containers(first: 100, after: $after) {\n      totalCount\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        code\n      }\n    }\n  }\n": types.ContainersDocument,
    "\n  query Temperatures($containerId: UUID!, $after: String) {\n    temperatures(containerId: $containerId, first: 100, after: $after) {\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        timestampUtc\n        celsius\n      }\n    }\n  }\n": types.TemperaturesDocument,
    "\n  query Documents {\n    documents(first: 100) {\n      nodes {\n        id\n        fileName\n        contentType\n        sizeBytes\n        status\n        createdAtUtc\n        downloadUrl\n      }\n    }\n  }\n": types.DocumentsDocument,
    "\n  mutation RequestDocumentUpload($input: RequestDocumentUploadInput!) {\n    requestDocumentUpload(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n        document {\n          id\n        }\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": types.RequestDocumentUploadDocument,
    "\n  mutation ConfirmDocumentUpload($input: ConfirmDocumentUploadInput!) {\n    confirmDocumentUpload(input: $input) {\n      document {\n        id\n        status\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": types.ConfirmDocumentUploadDocument,
    "\n  mutation RenameDocument($input: RenameDocumentInput!) {\n    renameDocument(input: $input) {\n      document {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": types.RenameDocumentDocument,
    "\n  mutation ReplaceDocumentContent($input: ReplaceDocumentContentInput!) {\n    replaceDocumentContent(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": types.ReplaceDocumentContentDocument,
    "\n  mutation DeleteDocument($input: DeleteDocumentInput!) {\n    deleteDocument(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": types.DeleteDocumentDocument,
    "\n  query Products {\n    products(first: 100, order: [{ name: ASC }]) {\n      totalCount\n      nodes {\n        id\n        name\n        sku\n        price\n        isArchived\n        rowVersion\n        updatedAtUtc\n        updatedBy\n      }\n    }\n  }\n": types.ProductsDocument,
    "\n  mutation CreateProduct($input: CreateProductInput!) {\n    createProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": types.CreateProductDocument,
    "\n  mutation UpdateProduct($input: UpdateProductInput!) {\n    updateProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n": types.UpdateProductDocument,
    "\n  mutation DeleteProduct($input: DeleteProductInput!) {\n    deleteProduct(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n": types.DeleteProductDocument,
    "\n  query ProductPermissions {\n    viewer {\n      permissions\n    }\n  }\n": types.ProductPermissionsDocument,
    "\n  query ProductAudit($productId: UUID!) {\n    productAudit(productId: $productId, first: 50) {\n      nodes {\n        id\n        change\n        name\n        price\n        occurredAtUtc\n      }\n    }\n  }\n": types.ProductAuditDocument,
    "\n  query Queues {\n    queues {\n      name\n      messages\n      consumers\n    }\n  }\n": types.QueuesDocument,
    "\n  mutation PurgeQueue($input: PurgeQueueInput!) {\n    purgeQueue(input: $input) {\n      unsignedInt\n    }\n  }\n": types.PurgeQueueDocument,
};

/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Viewer {\n    viewer {\n      email\n      permissions\n    }\n  }\n"): typeof import('./graphql').ViewerDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Containers($after: String) {\n    containers(first: 100, after: $after) {\n      totalCount\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        code\n      }\n    }\n  }\n"): typeof import('./graphql').ContainersDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Temperatures($containerId: UUID!, $after: String) {\n    temperatures(containerId: $containerId, first: 100, after: $after) {\n      pageInfo {\n        hasNextPage\n        endCursor\n      }\n      nodes {\n        id\n        timestampUtc\n        celsius\n      }\n    }\n  }\n"): typeof import('./graphql').TemperaturesDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Documents {\n    documents(first: 100) {\n      nodes {\n        id\n        fileName\n        contentType\n        sizeBytes\n        status\n        createdAtUtc\n        downloadUrl\n      }\n    }\n  }\n"): typeof import('./graphql').DocumentsDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation RequestDocumentUpload($input: RequestDocumentUploadInput!) {\n    requestDocumentUpload(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n        document {\n          id\n        }\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').RequestDocumentUploadDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation ConfirmDocumentUpload($input: ConfirmDocumentUploadInput!) {\n    confirmDocumentUpload(input: $input) {\n      document {\n        id\n        status\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').ConfirmDocumentUploadDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation RenameDocument($input: RenameDocumentInput!) {\n    renameDocument(input: $input) {\n      document {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').RenameDocumentDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation ReplaceDocumentContent($input: ReplaceDocumentContentInput!) {\n    replaceDocumentContent(input: $input) {\n      documentUploadPayload {\n        uploadUrl\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').ReplaceDocumentContentDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation DeleteDocument($input: DeleteDocumentInput!) {\n    deleteDocument(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').DeleteDocumentDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Products {\n    products(first: 100, order: [{ name: ASC }]) {\n      totalCount\n      nodes {\n        id\n        name\n        sku\n        price\n        isArchived\n        rowVersion\n        updatedAtUtc\n        updatedBy\n      }\n    }\n  }\n"): typeof import('./graphql').ProductsDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation CreateProduct($input: CreateProductInput!) {\n    createProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').CreateProductDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation UpdateProduct($input: UpdateProductInput!) {\n    updateProduct(input: $input) {\n      product {\n        id\n      }\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n        ... on ValidationError {\n          fields {\n            field\n            messages\n          }\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').UpdateProductDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation DeleteProduct($input: DeleteProductInput!) {\n    deleteProduct(input: $input) {\n      errors {\n        __typename\n        ... on Error {\n          message\n        }\n      }\n    }\n  }\n"): typeof import('./graphql').DeleteProductDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query ProductPermissions {\n    viewer {\n      permissions\n    }\n  }\n"): typeof import('./graphql').ProductPermissionsDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query ProductAudit($productId: UUID!) {\n    productAudit(productId: $productId, first: 50) {\n      nodes {\n        id\n        change\n        name\n        price\n        occurredAtUtc\n      }\n    }\n  }\n"): typeof import('./graphql').ProductAuditDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  query Queues {\n    queues {\n      name\n      messages\n      consumers\n    }\n  }\n"): typeof import('./graphql').QueuesDocument;
/**
 * The graphql function is used to parse GraphQL queries into a document that can be used by GraphQL clients.
 */
export function graphql(source: "\n  mutation PurgeQueue($input: PurgeQueueInput!) {\n    purgeQueue(input: $input) {\n      unsignedInt\n    }\n  }\n"): typeof import('./graphql').PurgeQueueDocument;


export function graphql(source: string) {
  return (documents as any)[source] ?? {};
}
