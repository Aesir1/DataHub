import { setupServer } from "msw/node";

/** MSW server for GraphQL (/api/graphql) and upload mocks; tests add handlers with server.use(). */
export const server = setupServer();
