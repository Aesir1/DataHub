/* eslint-disable */
/** Internal type. DO NOT USE DIRECTLY. */
type Exact<T extends { [key: string]: unknown }> = { [K in keyof T]: T[K] };
/** Internal type. DO NOT USE DIRECTLY. */
export type Incremental<T> = T | { [P in keyof T]?: P extends ' $fragmentName' | '__typename' ? T[P] : never };
import type { DocumentTypeDecoration } from '@graphql-typed-document-node/core';
export type ChangeType =
  | 'CREATED'
  | 'DELETED'
  | 'UPDATED';

export type ConfirmDocumentUploadInput = {
  id: string;
};

export type CreateProductInput = {
  name: string;
  price: number;
  sku: string;
};

export type DeleteDocumentInput = {
  id: string;
};

export type DeleteProductInput = {
  id: string;
};

export type DocumentStatus =
  | 'AVAILABLE'
  | 'PENDING';

export type PurgeQueueInput = {
  name: string;
};

export type RenameDocumentInput = {
  fileName: string;
  id: string;
};

export type ReplaceDocumentContentInput = {
  contentType: string;
  id: string;
  sizeBytes: number;
};

export type RequestDocumentUploadInput = {
  contentType: string;
  fileName: string;
  sizeBytes: number;
};

export type UpdateProductInput = {
  id: string;
  isArchived: boolean;
  name: string;
  price: number;
  rowVersion: number;
};

export type ViewerQueryVariables = Exact<{ [key: string]: never; }>;


export type ViewerQuery = { viewer: { email: string | null, permissions: Array<string> } };

export type ContainersQueryVariables = Exact<{
  after?: string | null | undefined;
}>;


export type ContainersQuery = { containers: { totalCount: number, pageInfo: { hasNextPage: boolean, endCursor: string | null }, nodes: Array<{ id: string, code: string }> | null } | null };

export type TemperaturesQueryVariables = Exact<{
  containerId: string;
  after?: string | null | undefined;
}>;


export type TemperaturesQuery = { temperatures: { pageInfo: { hasNextPage: boolean, endCursor: string | null }, nodes: Array<{ id: string, timestampUtc: string, celsius: number }> | null } | null };

export type DocumentsQueryVariables = Exact<{ [key: string]: never; }>;


export type DocumentsQuery = { documents: { nodes: Array<{ id: string, fileName: string, contentType: string, sizeBytes: number, status: DocumentStatus, createdAtUtc: string, downloadUrl: string | null }> | null } | null };

export type RequestDocumentUploadMutationVariables = Exact<{
  input: RequestDocumentUploadInput;
}>;


export type RequestDocumentUploadMutation = { requestDocumentUpload: { documentUploadPayload: { uploadUrl: string, document: { id: string } } | null, errors: Array<
      | { __typename: 'ForbiddenError', message: string }
      | { __typename: 'ValidationError', message: string, fields: Array<{ field: string, messages: Array<string> }> }
    > | null } };

export type ConfirmDocumentUploadMutationVariables = Exact<{
  input: ConfirmDocumentUploadInput;
}>;


export type ConfirmDocumentUploadMutation = { confirmDocumentUpload: { document: { id: string, status: DocumentStatus } | null, errors: Array<
      | { __typename: 'ConflictError', message: string }
      | { __typename: 'NotFoundError', message: string }
    > | null } };

export type RenameDocumentMutationVariables = Exact<{
  input: RenameDocumentInput;
}>;


export type RenameDocumentMutation = { renameDocument: { document: { id: string } | null, errors: Array<
      | { __typename: 'NotFoundError', message: string }
      | { __typename: 'ValidationError', message: string, fields: Array<{ field: string, messages: Array<string> }> }
    > | null } };

export type ReplaceDocumentContentMutationVariables = Exact<{
  input: ReplaceDocumentContentInput;
}>;


export type ReplaceDocumentContentMutation = { replaceDocumentContent: { documentUploadPayload: { uploadUrl: string } | null, errors: Array<
      | { __typename: 'NotFoundError', message: string }
      | { __typename: 'ValidationError', message: string, fields: Array<{ field: string, messages: Array<string> }> }
    > | null } };

export type DeleteDocumentMutationVariables = Exact<{
  input: DeleteDocumentInput;
}>;


export type DeleteDocumentMutation = { deleteDocument: { errors: Array<{ __typename: 'NotFoundError', message: string }> | null } };

export type ProductsQueryVariables = Exact<{ [key: string]: never; }>;


export type ProductsQuery = { products: { totalCount: number, nodes: Array<{ id: string, name: string, sku: string, price: number, isArchived: boolean, rowVersion: number, updatedAtUtc: string | null, updatedBy: string | null }> | null } | null };

export type CreateProductMutationVariables = Exact<{
  input: CreateProductInput;
}>;


export type CreateProductMutation = { createProduct: { product: { id: string } | null, errors: Array<
      | { __typename: 'ConflictError', message: string }
      | { __typename: 'ValidationError', message: string, fields: Array<{ field: string, messages: Array<string> }> }
    > | null } };

export type UpdateProductMutationVariables = Exact<{
  input: UpdateProductInput;
}>;


export type UpdateProductMutation = { updateProduct: { product: { id: string } | null, errors: Array<
      | { __typename: 'ConcurrencyError', message: string }
      | { __typename: 'NotFoundError', message: string }
      | { __typename: 'ValidationError', message: string, fields: Array<{ field: string, messages: Array<string> }> }
    > | null } };

export type DeleteProductMutationVariables = Exact<{
  input: DeleteProductInput;
}>;


export type DeleteProductMutation = { deleteProduct: { errors: Array<{ __typename: 'NotFoundError', message: string }> | null } };

export type ProductPermissionsQueryVariables = Exact<{ [key: string]: never; }>;


export type ProductPermissionsQuery = { viewer: { permissions: Array<string> } };

export type ProductAuditQueryVariables = Exact<{
  productId: string;
}>;


export type ProductAuditQuery = { productAudit: { nodes: Array<{ id: string, change: ChangeType, name: string, price: number, occurredAtUtc: string }> | null } | null };

export type QueuesQueryVariables = Exact<{ [key: string]: never; }>;


export type QueuesQuery = { queues: Array<{ name: string, messages: number, consumers: number }> };

export type PurgeQueueMutationVariables = Exact<{
  input: PurgeQueueInput;
}>;


export type PurgeQueueMutation = { purgeQueue: { unsignedInt: number | null } };

export class TypedDocumentString<TResult, TVariables>
  extends String
  implements DocumentTypeDecoration<TResult, TVariables>
{
  __apiType?: NonNullable<DocumentTypeDecoration<TResult, TVariables>['__apiType']>;
  private value: string;
  public __meta__?: Record<string, any> | undefined;

  constructor(value: string, __meta__?: Record<string, any> | undefined) {
    super(value);
    this.value = value;
    this.__meta__ = __meta__;
  }

  override toString(): string & DocumentTypeDecoration<TResult, TVariables> {
    return this.value;
  }
}

export const ViewerDocument = new TypedDocumentString(`
    query Viewer {
  viewer {
    email
    permissions
  }
}
    `) as unknown as TypedDocumentString<ViewerQuery, ViewerQueryVariables>;
export const ContainersDocument = new TypedDocumentString(`
    query Containers($after: String) {
  containers(first: 100, after: $after) {
    totalCount
    pageInfo {
      hasNextPage
      endCursor
    }
    nodes {
      id
      code
    }
  }
}
    `) as unknown as TypedDocumentString<ContainersQuery, ContainersQueryVariables>;
export const TemperaturesDocument = new TypedDocumentString(`
    query Temperatures($containerId: UUID!, $after: String) {
  temperatures(containerId: $containerId, first: 100, after: $after) {
    pageInfo {
      hasNextPage
      endCursor
    }
    nodes {
      id
      timestampUtc
      celsius
    }
  }
}
    `) as unknown as TypedDocumentString<TemperaturesQuery, TemperaturesQueryVariables>;
export const DocumentsDocument = new TypedDocumentString(`
    query Documents {
  documents(first: 100) {
    nodes {
      id
      fileName
      contentType
      sizeBytes
      status
      createdAtUtc
      downloadUrl
    }
  }
}
    `) as unknown as TypedDocumentString<DocumentsQuery, DocumentsQueryVariables>;
export const RequestDocumentUploadDocument = new TypedDocumentString(`
    mutation RequestDocumentUpload($input: RequestDocumentUploadInput!) {
  requestDocumentUpload(input: $input) {
    documentUploadPayload {
      uploadUrl
      document {
        id
      }
    }
    errors {
      __typename
      ... on Error {
        message
      }
      ... on ValidationError {
        fields {
          field
          messages
        }
      }
    }
  }
}
    `) as unknown as TypedDocumentString<RequestDocumentUploadMutation, RequestDocumentUploadMutationVariables>;
export const ConfirmDocumentUploadDocument = new TypedDocumentString(`
    mutation ConfirmDocumentUpload($input: ConfirmDocumentUploadInput!) {
  confirmDocumentUpload(input: $input) {
    document {
      id
      status
    }
    errors {
      __typename
      ... on Error {
        message
      }
    }
  }
}
    `) as unknown as TypedDocumentString<ConfirmDocumentUploadMutation, ConfirmDocumentUploadMutationVariables>;
export const RenameDocumentDocument = new TypedDocumentString(`
    mutation RenameDocument($input: RenameDocumentInput!) {
  renameDocument(input: $input) {
    document {
      id
    }
    errors {
      __typename
      ... on Error {
        message
      }
      ... on ValidationError {
        fields {
          field
          messages
        }
      }
    }
  }
}
    `) as unknown as TypedDocumentString<RenameDocumentMutation, RenameDocumentMutationVariables>;
export const ReplaceDocumentContentDocument = new TypedDocumentString(`
    mutation ReplaceDocumentContent($input: ReplaceDocumentContentInput!) {
  replaceDocumentContent(input: $input) {
    documentUploadPayload {
      uploadUrl
    }
    errors {
      __typename
      ... on Error {
        message
      }
      ... on ValidationError {
        fields {
          field
          messages
        }
      }
    }
  }
}
    `) as unknown as TypedDocumentString<ReplaceDocumentContentMutation, ReplaceDocumentContentMutationVariables>;
export const DeleteDocumentDocument = new TypedDocumentString(`
    mutation DeleteDocument($input: DeleteDocumentInput!) {
  deleteDocument(input: $input) {
    errors {
      __typename
      ... on Error {
        message
      }
    }
  }
}
    `) as unknown as TypedDocumentString<DeleteDocumentMutation, DeleteDocumentMutationVariables>;
export const ProductsDocument = new TypedDocumentString(`
    query Products {
  products(first: 100, order: [{ name: ASC }]) {
    totalCount
    nodes {
      id
      name
      sku
      price
      isArchived
      rowVersion
      updatedAtUtc
      updatedBy
    }
  }
}
    `) as unknown as TypedDocumentString<ProductsQuery, ProductsQueryVariables>;
export const CreateProductDocument = new TypedDocumentString(`
    mutation CreateProduct($input: CreateProductInput!) {
  createProduct(input: $input) {
    product {
      id
    }
    errors {
      __typename
      ... on Error {
        message
      }
      ... on ValidationError {
        fields {
          field
          messages
        }
      }
    }
  }
}
    `) as unknown as TypedDocumentString<CreateProductMutation, CreateProductMutationVariables>;
export const UpdateProductDocument = new TypedDocumentString(`
    mutation UpdateProduct($input: UpdateProductInput!) {
  updateProduct(input: $input) {
    product {
      id
    }
    errors {
      __typename
      ... on Error {
        message
      }
      ... on ValidationError {
        fields {
          field
          messages
        }
      }
    }
  }
}
    `) as unknown as TypedDocumentString<UpdateProductMutation, UpdateProductMutationVariables>;
export const DeleteProductDocument = new TypedDocumentString(`
    mutation DeleteProduct($input: DeleteProductInput!) {
  deleteProduct(input: $input) {
    errors {
      __typename
      ... on Error {
        message
      }
    }
  }
}
    `) as unknown as TypedDocumentString<DeleteProductMutation, DeleteProductMutationVariables>;
export const ProductPermissionsDocument = new TypedDocumentString(`
    query ProductPermissions {
  viewer {
    permissions
  }
}
    `) as unknown as TypedDocumentString<ProductPermissionsQuery, ProductPermissionsQueryVariables>;
export const ProductAuditDocument = new TypedDocumentString(`
    query ProductAudit($productId: UUID!) {
  productAudit(productId: $productId, first: 50) {
    nodes {
      id
      change
      name
      price
      occurredAtUtc
    }
  }
}
    `) as unknown as TypedDocumentString<ProductAuditQuery, ProductAuditQueryVariables>;
export const QueuesDocument = new TypedDocumentString(`
    query Queues {
  queues {
    name
    messages
    consumers
  }
}
    `) as unknown as TypedDocumentString<QueuesQuery, QueuesQueryVariables>;
export const PurgeQueueDocument = new TypedDocumentString(`
    mutation PurgeQueue($input: PurgeQueueInput!) {
  purgeQueue(input: $input) {
    unsignedInt
  }
}
    `) as unknown as TypedDocumentString<PurgeQueueMutation, PurgeQueueMutationVariables>;